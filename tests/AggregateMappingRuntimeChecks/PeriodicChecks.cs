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

internal static class PeriodicChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, IConfiguration configuration,
        string run, AggregateRecipeDto prototype, Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct)
    {
        var payload = new WorkReportPayloadService(db); var store = new AggregateMongoStore(db, runner, payload, payload);
        var f = await PeriodicFixture.Seed(db, store, run, ct); var actor = f.Root.Actor;
        void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException(name); Console.WriteLine("PASS periodic " + name); }
        Task<JsonElement> Call(string path, object body, HttpStatusCode status = HttpStatusCode.OK) => post("aggregate-v2/" + path, body, actor, status);
        async Task<AggregatePeriodContextDto> Context(int month) => (await Call("editor/bootstrap", new { reportId = f.Targets[month].Id }))
            .GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        async Task<AggregateInstanceState> State(string id) => (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, id, token), ct))!.Value;
        async Task<decimal> Value(int month)
        {
            var report = await db.WorkAssignmentReports.Find(r => r.Id == f.Targets[month].Id).SingleAsync(ct);
            var saved = await payload.LoadReportPayloadAsync(report, ct); WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, saved);
            return JsonDocument.Parse(saved.FieldValuesJson!).RootElement.GetProperty("total").GetDecimal();
        }
        AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
        var recipe = prototype with { Nodes = prototype.Nodes.Select(n => n.Kind == "SOURCE" ? n with { Form = Pin(f.Root.SourceForm) }
            : n.Kind == "TARGET" ? n with { Form = Pin(f.Root.TargetForm) } : n).ToList() };
        AggregateExpressionDto Plus(int n) => new() { Kind = "BINARY", Name = "+", Arguments = [new() { Kind = "CALL", Name = "SUM", Arguments = [new() { Kind = "INPUT", Ref = "in" }] }, new() { Kind = "NUMBER", Value = n.ToString() }] };
        var context0 = await Context(0); var context1 = await Context(1);
        Check(context0.Kind == "PERIODIC" && context1.DataEndDate == "2026-10-31" && context1.PeriodKey == "20261030", "data window is separate from due-day period identity");
        var created = await Call("configs", new AggregateConfigCreateCommandDto(context0, new(run + "-pc", context0.BindingId, Pin(f.Root.TargetForm), recipe)));
        var configId = created.GetProperty("id").GetString()!;
        async Task<string> Instance(int month) => (await Call("instances", new AggregateInstanceCreateCommandDto(run + "-pi" + month, await Context(month), configId))).GetProperty("id").GetString()!;
        var ids = new[] { await Instance(0), await Instance(1), "" };
        var serial = 0;
        async Task<JsonElement> Apply(int month, AggregateInstanceOverrideDto? overlay = null, bool reset = false)
        {
            var state = await State(ids[month]); var ctx = await Context(month);
            var change = new AggregateMappingChangeDto(state.Revision, overlay, state.Selection, [], reset);
            var preview = await Call($"instances/{state.Id}/mapping/preview", new AggregateMappingPreviewCommandDto(ctx, change));
            await Call($"instances/{state.Id}/apply", new AggregateMappingApplyCommandDto(run + "-pa" + ++serial, ctx, change, preview.GetProperty("token").GetString()!));
            return preview;
        }
        var p0 = await Apply(0); var p1 = await Apply(1);
        Check(await Value(0) == 10 && await Value(1) == 20, "same config resolves different monthly source payloads");
        Check(p0.GetProperty("preview").GetProperty("preview").GetProperty("linkedSources")[0].GetProperty("reportId").GetString() == f.Sources[0].Id
            && p1.GetProperty("preview").GetProperty("preview").GetProperty("linkedSources")[0].GetProperty("reportId").GetString() == f.Sources[1].Id,
            "lineage is bound to each current occurrence, not the previous report");
        async Task Transition(int month, string operation, WorkAssignmentReportStatus status)
        {
            string? confirmation = null;
            if (operation == "SUBMIT") confirmation = (await Call($"reports/{f.Targets[month].Id}/submission-preview", new AggregateInstanceReadCommandDto(await Context(month)))).GetProperty("token").GetString();
            await runner.ExecuteAsync(async (session, token) => {
                var before = await db.WorkAssignmentReports.Find(session, r => r.Id == f.Targets[month].Id).SingleAsync(token);
                await AggregateHostIntegration.LifecycleAsync(db, session, runner, before, operation, actor, run + "-" + operation + month,
                    status, before.LifecycleRevision + 1, token, configuration, run + "-session", confirmation);
                await db.WorkAssignmentReports.UpdateOneAsync(session, r => r.Id == before.Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.Status, status).Inc(r => r.LifecycleRevision, 1), cancellationToken: token);
                return true;
            }, ct);
        }
        await Transition(0, "SUBMIT", WorkAssignmentReportStatus.Submitted);
        var frozenBefore = (await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(new BsonDocument("target", f.Targets[0].Id)).SingleAsync(ct)).ToJson();
        var revision2 = recipe with { Nodes = recipe.Nodes.Select(n => n.Id == "c" ? n with { Expressions = [new("out", Plus(1))] } : n).ToList() };
        var impact = new AggregateConfigImpactRequestDto(1, revision2, [new(ids[1], (await State(ids[1])).Revision)], []);
        var reviewed = await Call($"configs/{configId}/impact-preview", new AggregateConfigImpactCommandDto(await Context(1), impact));
        Check(reviewed.GetProperty("plan").GetProperty("conflicts").GetArrayLength() == 0, "r2 impact preview accepts only the selected draft");
        await Call($"configs/{configId}/revisions", new AggregateConfigRevisionCommandDto(run + "-pr2", await Context(1), impact, reviewed.GetProperty("token").GetString()!));
        Check((await State(ids[0])).ConfigRevision == 1 && (await State(ids[1])).ConfigRevision == 2 && await Value(0) == 10 && await Value(1) == 21,
            "r2 migrates selected draft while submitted month retains r1 and native value");
        Check(frozenBefore == (await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(new BsonDocument("target", f.Targets[0].Id)).SingleAsync(ct)).ToJson(),
            "r2 leaves frozen submission byte-for-byte unchanged");
        ids[2] = await Instance(2); await Apply(2);
        Check((await State(ids[2])).ConfigRevision == 2 && await Value(2) == 41, "new monthly instance uses current config head r2");
        var version = (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateConfigVersion>(AggregateCollections.Versions, AggregateCommandService.VersionKey(configId, 2), token), ct))!.Value;
        var formsBefore = await db.DynamicFormTemplates.CountDocumentsAsync(t => t.CreatedByUserId == actor, cancellationToken: ct);
        await Apply(1, new(version.RecipeHash, [], [new("c", "out", Plus(5))], []));
        Check(await Value(1) == 25 && (await State(ids[1])).ConfigRevision == 2, "per-period formula override keeps shared revision pin");
        await Apply(1, reset: true);
        Check(await Value(1) == 21 && (await State(ids[1])).Overrides == null
            && await db.DynamicFormTemplates.CountDocumentsAsync(t => t.CreatedByUserId == actor, cancellationToken: ct) == formsBefore,
            "preview-confirm reset restores shared formula without auxiliary Forms");
        var rule = recipe.TimeRules.Single();
        await Apply(1, new(version.RecipeHash, [rule with { Mode = "EXPLICIT_RANGE", StartDate = "2026-09-01", EndDate = "2026-09-30" }], [], []));
        Check(await Value(1) == 11, "fixed dates stay in September when editing October report");
        await Apply(1, new(version.RecipeHash, [rule with { Mode = "CUMULATIVE_FROM", StartDate = "2026-09-01" }], [], []));
        Check(await Value(1) == 31, "cumulative period uses September plus October and pinned formula");
        await Transition(1, "SUBMIT", WorkAssignmentReportStatus.Submitted);
        async Task<int> Owners() => (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateLockState>(AggregateCollections.Locks, "REPORT:" + f.Sources[0].Id, token), ct))!.Value.Owners.Count;
        Check(await Owners() == 2, "same source report has two independent submitted owners");
        await Transition(0, "RETURN", WorkAssignmentReportStatus.Draft);
        Check(await Owners() == 1 && (await State(ids[1])).State == "FROZEN", "returning first period retains the second owner's lock and frozen mapping");
        try
        {
            await runner.ExecuteAsync(async (session, token) => { await AggregateHostIntegration.GuardReportAsync(db, session, f.Sources[0], token); return true; }, ct);
            throw new InvalidOperationException("remaining owner lock not enforced");
        }
        catch (AggregatePreviewException ex) when (ex.Code == "AGG_SOURCE_LOCKED") { Check(true, "remaining owner continues to block source mutation"); }
        await Transition(1, "RETURN", WorkAssignmentReportStatus.Draft);
        Check(await Owners() == 0, "last returning owner releases source without changing child status");
        // Two distinct user commands race the same reviewed instance revision through HTTP.
        var raceState = await State(ids[2]); var raceContext = await Context(2);
        var raceChange = new AggregateMappingChangeDto(raceState.Revision, null, raceState.Selection, [], false);
        var racePreview = await Call($"instances/{ids[2]}/mapping/preview", new AggregateMappingPreviewCommandDto(raceContext, raceChange));
        async Task<bool> Race(int index)
        {
            try { await Call($"instances/{ids[2]}/apply", new AggregateMappingApplyCommandDto(run + "-race" + index, raceContext, raceChange, racePreview.GetProperty("token").GetString()!)); return true; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("got 409:") && (ex.Message.Contains("AGG_REVISION_CONFLICT") || ex.Message.Contains("AGG_CONFIRMATION_STALE"))) { return false; }
        }
        var race = await Task.WhenAll(Race(1), Race(2));
        Check(race.Count(x => x) == 1 && (await State(ids[2])).Revision == raceState.Revision + 1 && await Value(2) == 41,
            "concurrent HTTP Apply has one winner and one native mapping revision increment");
        async Task<JsonElement> RawPreview(AggregateRecipeDto? useRecipe = null)
        {
            var boot = await Call("editor/bootstrap", new { reportId = f.Targets[2].Id });
            var ctx = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            var revisions = boot.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
            return (await Call($"instances/{ids[2]}/preview", new AggregatePreviewRequestDto(ctx, ids[2], configId, revisions, useRecipe ?? recipe,
                new([new("s", "FORM_SELECTOR", [], [])])))).GetProperty("preview");
        }
        // An older Approved version with a deliberately different value must not be used
        // when the current source is Submitted. This setup is not a report-version API test.
        var old = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WorkAssignmentReport>(f.Sources[2].ToBson());
        old.Id = ObjectId.GenerateNewId().ToString(); old.IsCurrent = false; old.PayloadRevision = 0;
        var oldPayload = await payload.SaveReportPayloadAsync(old, "[]", "{\"n\":999}", null, null, actor, DateTime.UtcNow, ct);
        old.PayloadRevision = oldPayload.PayloadRevision; old.PayloadHash = oldPayload.PayloadHash; old.PayloadSizeBytes = oldPayload.PayloadSizeBytes; old.PayloadStatus = oldPayload.PayloadStatus;
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Sources[2].Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.VersionNo, 2), cancellationToken: ct);
        await db.WorkAssignmentReports.InsertOneAsync(old, cancellationToken: ct);
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Sources[2].Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted).Inc(r => r.LifecycleRevision, 1), cancellationToken: ct);
        var eligibility = await RawPreview();
        Check(eligibility.GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT"
            && eligibility.GetProperty("contributingSources").GetArrayLength() == 0,
            "Submitted current source never falls back to older Approved version or fabricated zero");
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Sources[2].Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Approved).Set(r => r.IsCurrent, false), cancellationToken: ct);
        await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == f.Sources[2].WorkReportPeriodId,
            Builders<WorkReportPeriod>.Update.Set(p => p.CurrentReportId, null), cancellationToken: ct);
        eligibility = await RawPreview();
        Check(eligibility.GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT"
            && eligibility.GetProperty("coverage").EnumerateArray().Any(s => s.GetProperty("state").GetString() == "MISSING"),
            "unmaterialized occurrence is MISSING coverage with no numeric zero");
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Sources[2].Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.IsCurrent, true), cancellationToken: ct);
        await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == f.Sources[2].WorkReportPeriodId,
            Builders<WorkReportPeriod>.Update.Set(p => p.CurrentReportId, f.Sources[2].Id), cancellationToken: ct);
        async Task SourceValue(string fields)
        {
            var current = await db.WorkAssignmentReports.Find(r => r.Id == f.Sources[2].Id).SingleAsync(ct);
            var saved = await payload.SaveReportPayloadAsync(current, "[]", fields, null, null, actor, DateTime.UtcNow, ct);
            await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == current.Id, Builders<WorkAssignmentReport>.Update
                .Set(r => r.PayloadRevision, saved.PayloadRevision).Set(r => r.PayloadHash, saved.PayloadHash)
                .Set(r => r.PayloadSizeBytes, saved.PayloadSizeBytes).Set(r => r.PayloadStatus, saved.PayloadStatus), cancellationToken: ct);
        }
        await SourceValue("{}"); eligibility = await RawPreview();
        Check(eligibility.GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT", "blank Approved payload stays NO_RESULT");
        await SourceValue("{\"n\":0}"); eligibility = await RawPreview();
        Check(eligibility.GetProperty("results")[0].GetProperty("state").GetString() == "RESULT"
            && eligibility.GetProperty("results")[0].GetProperty("value").GetString() == "0", "actual numeric zero stays RESULT zero");
        await SourceValue("{\"n\":40}");
        var overlapRecipe = recipe with { TimeRules = [rule with { Mode = "EXPLICIT_RANGE", StartDate = "2026-10-15", EndDate = "2026-11-15" }] };
        var contained = await RawPreview(overlapRecipe);
        Check(contained.GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT", "contained window excludes both partially overlapping monthly reports");
        var overlap = await RawPreview(overlapRecipe with { TimeRules = [overlapRecipe.TimeRules[0] with { Match = "OVERLAPS_WHOLE_REPORT" }] });
        Check(overlap.GetProperty("results")[0].GetProperty("value").GetString() == "60"
            && overlap.GetProperty("contributingSources").GetArrayLength() == 2, "whole-overlap takes both reports completely without prorating values");
        var currentState = await State(ids[2]);
        await store.ExecuteAsync(async (tx, token) => { await AggregateRefreshService.EnqueueAsync(tx, currentState, run + "-before-unlink", token); return true; }, ct);
        var unlink = await Call($"instances/{ids[2]}/unlink-preview", new AggregateUnlinkPreviewCommandDto(await Context(2), currentState.Revision));
        await Call($"instances/{ids[2]}/unlink", new AggregateUnlinkAllCommandDto(run + "-unlink", await Context(2), currentState.Revision, unlink.GetString()!));
        var refresh = new AggregateRefreshService(store, new AggregateMongoCommandReader(db, payload, true));
        Check(await refresh.RunAsync(ids[2], currentState.Generation, ct) == "DISCARDED"
            && (await State(ids[2])).State == "UNLINKED" && await Value(2) == 41,
            "queued old-generation refresh after unlink is discarded and preserves manual native value");
    }
}
