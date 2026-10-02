using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class RecoveryChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, string run, AggregateRecipeDto prototype,
        Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, Func<string, object, string?, HttpStatusCode, Task<JsonElement>> put, CancellationToken ct)
    {
        var payload = new WorkReportPayloadService(db); var store = new AggregateMongoStore(db, runner, payload, payload);
        var f = await PeriodicFixture.Seed(db, store, run + "-repair", ct); var actor = f.Root.Actor;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS recovery " + name); }
        Task<JsonElement> Call(string path, object body, HttpStatusCode status = HttpStatusCode.OK) => post("aggregate-v2/" + path, body, actor, status);
        async Task<AggregatePeriodContextDto> Context(int i) => (await Call("editor/bootstrap", new { reportId = f.Targets[i].Id })).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
        var recipe = prototype with { Nodes = prototype.Nodes.Select(n => n.Kind == "SOURCE" ? n with { Form = Pin(f.Root.SourceForm) }
            : n.Kind == "TARGET" ? n with { Form = Pin(f.Root.TargetForm) } : n).ToList() };
        AggregateExpressionDto Plus(int amount) => new() { Kind = "BINARY", Name = "+", Arguments = [new() { Kind = "CALL", Name = "SUM", Arguments = [new() { Kind = "INPUT", Ref = "in" }] }, new() { Kind = "NUMBER", Value = amount.ToString() }] };
        var ctx = await Context(0);
        var config = await Call("configs", new AggregateConfigCreateCommandDto(ctx, new(run + "-repair-config", ctx.BindingId, Pin(f.Root.TargetForm), recipe)));
        var configId = config.GetProperty("id").GetString()!;
        async Task<string> Create(int i) => (await Call("instances", new AggregateInstanceCreateCommandDto(run + "-repair-instance" + i, await Context(i), configId))).GetProperty("id").GetString()!;
        var ids = new[] { await Create(0), await Create(1) };
        async Task<AggregateInstanceState> State(int i) => (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, ids[i], token), ct))!.Value;
        async Task<AggregateConfigVersion> Version(long revision) => (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateConfigVersion>(AggregateCollections.Versions, AggregateCommandService.VersionKey(configId, revision), token), ct))!.Value;
        async Task<string> Native()
        {
            var r = await db.WorkAssignmentReports.Find(r => r.Id == f.Targets[0].Id).SingleAsync(ct);
            var p = await payload.LoadReportPayloadAsync(r, ct);
            return JsonSerializer.Serialize(new { r.PayloadRevision, r.PayloadHash, p.FieldValuesJson, p.TableValuesJson });
        }
        async Task<decimal> Value()
        {
            var r = await db.WorkAssignmentReports.Find(r => r.Id == f.Targets[0].Id).SingleAsync(ct);
            return JsonDocument.Parse((await payload.LoadReportPayloadAsync(r, ct)).FieldValuesJson!).RootElement.GetProperty("total").GetDecimal();
        }
        async Task<string> Rows()
        {
            var rows = new List<string>();
            foreach (var name in AggregateCollections.All)
                rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", f.Root.WorkId)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
            return string.Join('\n', rows) + await Native();
        }
        var serial = 0;
        async Task Apply(AggregateInstanceOverrideDto? overlay, bool reset = false)
        {
            var state = await State(0); ctx = await Context(0);
            var change = new AggregateMappingChangeDto(state.Revision, overlay, state.Selection, [], reset);
            var preview = await Call($"instances/{ids[0]}/mapping/preview", new AggregateMappingPreviewCommandDto(ctx, change));
            await Call($"instances/{ids[0]}/apply", new AggregateMappingApplyCommandDto(run + "-repair-apply" + ++serial, ctx, change, preview.GetProperty("token").GetString()!));
        }
        await Apply(new((await Version(1)).RecipeHash, [], [new("c", "out", Plus(5))], []));
        Check(await Value() == 15, "fixture has an applied period override before shared revision change");
        var r2 = recipe with { Nodes = recipe.Nodes.Select(n => n.Id == "c" ? n with { Expressions = [new("out", Plus(1))] } : n).ToList() };
        var impact = new AggregateConfigImpactRequestDto(1, r2, [new(ids[0], (await State(0)).Revision)], []);
        var before = await Rows();
        var review = await Call($"configs/{configId}/impact-preview", new AggregateConfigImpactCommandDto(ctx, impact));
        Check(review.GetProperty("token").ValueKind == JsonValueKind.Null
            && review.GetProperty("plan").GetProperty("conflicts")[0].GetProperty("code").GetString() == "AGG_OVERRIDE_CONFLICT"
            && before == await Rows(), "conflicting shared revision returns explicit conflict without token or writes");
        Check(!review.GetRawText().Contains(f.Sources[0].Id) && !review.GetRawText().Contains(run + "-session"), "conflict-only preview withholds historical child evidence and server session capture");
        var denied = await Call($"configs/{configId}/revisions", new AggregateConfigRevisionCommandDto(run + "-unresolved", ctx, impact, "unresolved"), HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_REBASE_REQUIRED" && before == await Rows(), "unresolved conflict cannot publish new head or change native payload");
        impact = impact with { Resolutions = [new(ids[0], "c", "out", null, "KEEP_OVERRIDE")] };
        review = await Call($"configs/{configId}/impact-preview", new AggregateConfigImpactCommandDto(ctx, impact));
        await Call($"configs/{configId}/revisions", new AggregateConfigRevisionCommandDto(run + "-keep", ctx, impact, review.GetProperty("token").GetString()!));
        Check((await State(0)).ConfigRevision == 2 && await Value() == 15 && (await State(1)).ConfigRevision == 1,
            "KEEP_OVERRIDE migrates chosen draft to r2 while unselected draft remains on r1");
        var pinned = await Call($"configs/{configId}/read", new AggregateConfigReadCommandDto(await Context(1), 1));
        Check(pinned.GetProperty("head").GetProperty("headRevision").GetInt64() == 2 && pinned.GetProperty("version").GetProperty("revision").GetInt64() == 1,
            "read returns exact pinned r1 even when config head is r2");
        var r3 = r2 with { Nodes = r2.Nodes.Select(n => n.Id == "c" ? n with { Expressions = [new("out", Plus(2))] } : n).ToList() };
        impact = new(2, r3, [new(ids[0], (await State(0)).Revision)], [new(ids[0], "c", "out", null, "USE_NEW_BASE")]);
        review = await Call($"configs/{configId}/impact-preview", new AggregateConfigImpactCommandDto(ctx, impact));
        await Call($"configs/{configId}/revisions", new AggregateConfigRevisionCommandDto(run + "-newbase", ctx, impact, review.GetProperty("token").GetString()!));
        Check((await State(0)).ConfigRevision == 3 && (await State(0)).Overrides!.Expressions.Count == 0 && await Value() == 12,
            "USE_NEW_BASE resolves conflict explicitly and writes reviewed new formula");
        // Preserve the exact invalid user text; it is neither discarded nor evaluated.
        var raw = "{\"nodes\":[\"bản nháp chưa xong\"],";
        var stateBeforeRaw = await State(0); var nativeBeforeRaw = await Native();
        await put($"aggregate-v2/instances/{ids[0]}/raw-draft", new AggregateRawDraftCommandDto(run + "-raw", ctx, stateBeforeRaw.Revision, raw), actor, HttpStatusCode.OK);
        var read = await Call($"instances/{ids[0]}/read", new AggregateInstanceReadCommandDto(ctx));
        Check(read.GetProperty("instance").GetProperty("rawDraft").GetProperty("rawJson").GetString() == raw
            && read.GetProperty("instance").GetProperty("rawDraft").GetProperty("issues").GetArrayLength() > 0 && read.GetProperty("resultFreshness").GetString() == "NEEDS_REPAIR"
            && nativeBeforeRaw == await Native(), "invalid raw draft survives HTTP save/read with issues and unchanged native values");
        var reader = new AggregateMongoCommandReader(db, payload, true); var refresh = new AggregateRefreshService(store, reader); var rawState = await State(0);
        await store.ExecuteAsync(async (tx, token) => { await AggregateRefreshService.EnqueueAsync(tx, rawState, run + "-raw-refresh", token); return true; }, ct);
        Check(await refresh.RunAsync(rawState.Id, rawState.Generation, ct) == "DISCARDED" && nativeBeforeRaw == await Native(), "refresh cannot overwrite a NEEDS_REPAIR raw draft");
        // Reset is an explicit preview/confirm operation, including recovery from raw input.
        await Apply(null, reset: true);
        Check((await State(0)).RawDraft == null && (await State(0)).State == "DRAFT" && await Value() == 12,
            "preview-confirm reset repairs raw mapping using pinned r3 without creating Forms");
        // Simulate a stale/corrupt source schema pin, never a production publish operation.
        var schemaState = await State(0);
        var schemaChange = new AggregateMappingChangeDto(schemaState.Revision, null, schemaState.Selection, [], false);
        var schemaPreview = await Call($"instances/{ids[0]}/mapping/preview", new AggregateMappingPreviewCommandDto(ctx, schemaChange));
        var schemaNative = await Native();
        try
        {
            await db.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.Root.SourceForm.Id,
                Builders<DynamicFormTemplate>.Update.Set(t => t.PublishedSchemaHash, "p05-incompatible-schema"), cancellationToken: ct);
            before = await Rows();
            denied = await Call($"instances/{ids[0]}/mapping/preview", new AggregateMappingPreviewCommandDto(ctx, schemaChange), HttpStatusCode.Conflict);
            Check(denied.GetProperty("code").GetString() == "AGG_SCHEMA_INCOMPATIBLE" && before == await Rows(),
                "source schema drift rejects preview without replacing mapping or native values");
            denied = await Call($"instances/{ids[0]}/apply", new AggregateMappingApplyCommandDto(run + "-stale-schema", ctx,
                schemaChange, schemaPreview.GetProperty("token").GetString()!), HttpStatusCode.Conflict);
            Check(denied.GetProperty("code").GetString() == "AGG_SCHEMA_INCOMPATIBLE" && before == await Rows(),
                "confirmation obtained before schema drift cannot commit after the pin changes");
            await store.ExecuteAsync(async (tx, token) => { await AggregateRefreshService.EnqueueAsync(tx, schemaState, run + "-schema-refresh", token); return true; }, ct);
            Check(await refresh.RunAsync(schemaState.Id, schemaState.Generation, ct) == "FAILED"
                && schemaNative == await Native() && (await State(0)).Revision == schemaState.Revision,
                "schema-incompatible refresh records failure and preserves draft mapping and native result");
        }
        finally
        {
            var restoredPin = await db.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.Root.SourceForm.Id && t.PublishedSchemaHash == "p05-incompatible-schema",
                Builders<DynamicFormTemplate>.Update.Set(t => t.PublishedSchemaHash, f.Root.SourceForm.PublishedSchemaHash), cancellationToken: CancellationToken.None);
            if (restoredPin.MatchedCount != 1 && !await db.DynamicFormTemplates.Find(t => t.Id == f.Root.SourceForm.Id && t.PublishedSchemaHash == f.Root.SourceForm.PublishedSchemaHash).AnyAsync(CancellationToken.None))
                throw new InvalidOperationException("Fixture restore conflict; preserve newer Form changes");
            await FixtureProvenanceChecks.Migration(db, "after corrupt schema pin restored", CancellationToken.None);
        }
        var restored = await Call($"instances/{ids[0]}/mapping/preview", new AggregateMappingPreviewCommandDto(ctx, schemaChange));
        Check(restored.GetProperty("preview").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "12"
            && schemaNative == await Native(), "restored exact source pin can be previewed again without rewriting saved value");
        // Revoke the direct-child relationship after a successful Apply. Saved target values
        // remain readable but old child lineage must not become a read entitlement.
        await db.WorkAssignments.UpdateOneAsync(a => a.Id == f.Sources[0].WorkAssignmentId,
            Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, null), cancellationToken: ct);
        before = await Rows();
        read = await Call($"instances/{ids[0]}/read", new AggregateInstanceReadCommandDto(ctx));
        Check(read.GetProperty("instance").GetProperty("applied").ValueKind == JsonValueKind.Null
            && read.GetProperty("lastResults")[0].GetProperty("lineageRef").GetString() == "" && !read.GetRawText().Contains(f.Sources[0].Id)
            && before == await Rows(), "saved mapping read after source scope revocation returns target value without child lineage or writes");
        var current = await State(0);
        denied = await Call($"instances/{ids[0]}/mapping/preview", new AggregateMappingPreviewCommandDto(ctx,
            new(current.Revision, null, current.Selection, [], false)), HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_SOURCE_UNAVAILABLE" && before == await Rows(), "new preview after source scope revocation fails closed without writes");
    }
}
