using System.Text.Json;
using Hangfire;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.AggregateMapping.Persistence;

public sealed record AggregatePreviewJobRequest(string Kind, AggregatePeriodContextDto Context,
    AggregatePreviewRequestDto? Recipe, string? InstanceId, AggregateMappingChangeDto? Change,
    string? ConfigId = null, AggregateConfigImpactRequestDto? Impact = null, string? FormulaNodeId = null);

// A derived preview artifact, separate from report data and immutable submission snapshots.
// No bearer token, admin identity, native payload writes or second evaluator.
internal sealed class AggregatePreviewJobs(MongoDbContext db, IWorkReportPayloadReader payloads, IConfiguration config)
{
    internal const string Collection = "aggregate_preview_jobs";
    private IMongoCollection<BsonDocument> Rows => db.Db.GetCollection<BsonDocument>(Collection);
    internal async Task<string> Start(AggregatePreviewJobRequest request, string actor, string session, CancellationToken ct)
    {
        Validate(request);
        var stamp = await Stamp(request, actor, session, ct);
        await Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
            .Ascending("actor").Ascending("session").Ascending("target").Descending("createdAt"), new() { Name = "aggregate_preview_owner_current" }), cancellationToken: ct);
        var scope = request.Context.View == null
            ? AggregateCanonical.Hash(new { actor, session, request.Context.ReportId, request.Kind })
            : AggregateCanonical.Hash(new { actor, session, ViewId = AggregateTargetIdentity.Id(request.Context), request.Kind });
        var id = AggregateCanonical.Hash(new { scope, request, stamp });
        var row = new BsonDocument { ["_id"] = id, ["scope"] = scope, ["actor"] = actor, ["session"] = session,
            ["workId"] = request.Context.WorkId, ["target"] = AggregateTargetIdentity.Id(request.Context)!, ["stamp"] = stamp,
            ["request"] = JsonSerializer.Serialize(request, AggregateCanonical.Json), ["kind"] = request.Kind, ["state"] = "QUEUED",
            ["createdAt"] = DateTime.UtcNow, ["updatedAt"] = DateTime.UtcNow, ["attempt"] = 0 };
        try { await Rows.InsertOneAsync(row, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var previous = await Rows.FindOneAndUpdateAsync(new BsonDocument { ["_id"] = id, ["$or"] = new BsonArray {
                new BsonDocument("state", new BsonDocument("$in", new BsonArray { "FAILED", "CANCELLED" })),
                new BsonDocument { ["state"] = "COMPLETED", ["updatedAt"] = new BsonDocument("$lt", DateTime.UtcNow.AddMinutes(-3)) } } },
                new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "QUEUED", ["updatedAt"] = DateTime.UtcNow },
                    ["$unset"] = new BsonDocument { ["progress"] = "", ["result"] = "", ["error"] = "", ["errorPath"] = "", ["errorLocation"] = "", ["dispatchUntil"] = "" } },
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.Before }, ct);
            if (previous != null && previous["state"] == "COMPLETED")
            {
                // Keep the preceding complete preview separately when an expired confirmation is recomputed.
                previous["_id"] = AggregateCanonical.Hash(new { id, CompletedAt = previous["updatedAt"].ToUniversalTime() });
                previous["archived"] = true;
                await Rows.InsertOneAsync(previous, cancellationToken: ct);
            }
        }
        return id;
    }
    internal async Task<object> Read(string id, string actor, string session, CancellationToken ct)
    {
        var row = await Owned(id, actor, session, ct);
        var request = Request(row);
        // Recheck all source scopes, schema/config/revisions before returning even partial values.
        if (row["stamp"].AsString != await Stamp(request, actor, session, ct))
            throw new AggregatePreviewException("AGG_INPUT_STALE");
        JsonElement? Decode(string field) => row.TryGetValue(field, out var value) && value.IsString
            ? AggregateListTransport.Compact(JsonSerializer.Deserialize<JsonElement>(value.AsString)) : null;
        return new { Id = id, Kind = request.Kind, Context = request.Context, State = row["state"].AsString, Attempt = row["attempt"].AsInt32,
            Progress = row["state"] == "RUNNING" ? Decode("progress") : null, Result = row["state"] == "COMPLETED" ? Decode("result") : null,
            ErrorCode = row.GetValue("error", BsonNull.Value).IsString ? row["error"].AsString : null,
            ErrorPath = row["state"] == "FAILED" && row.GetValue("errorPath", BsonNull.Value).IsString ? row["errorPath"].AsString : null,
            ErrorLocation = row["state"] == "FAILED" && row.GetValue("errorLocation", BsonNull.Value).IsString
                ? JsonSerializer.Deserialize<AggregateErrorLocation>(row["errorLocation"].AsString, AggregateCanonical.Json) : null,
            UpdatedAtUtc = row["updatedAt"].ToUniversalTime().ToString("O") };
    }
    internal async Task AuthorizeContent(string id, string reportId, AggregateContentReference reference,
        string actor, string session, CancellationToken ct)
    {
        var row = await Owned(id, actor, session, ct);
        var request = Request(row);
        if ((request.Kind != "CONFIG" && AggregateTargetIdentity.Id(request.Context) != reportId) || row["state"] != "COMPLETED")
            throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
        if (row["stamp"].AsString != await Stamp(request, actor, session, ct)) throw new AggregatePreviewException("AGG_INPUT_STALE");
        using var result = JsonDocument.Parse(row["result"].AsString);
        bool Contains(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(Contains);
            if (value.ValueKind != JsonValueKind.Object) return false;
            if (value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == AggregateContentTableStore.Kind)
                return value.Deserialize<AggregateContentReference>(AggregateCanonical.Json) == reference;
            return value.EnumerateObject().Any(p => Contains(p.Value));
        }
        var authorized=request.Kind=="CONFIG"
            ? result.RootElement.GetProperty("plan").GetProperty("previews").EnumerateObject().Any(p=>
                AggregateTargetIdentity.Id(p.Value.GetProperty("preview").GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!)==reportId&&Contains(p.Value))
            : Contains(result.RootElement);
        if (!authorized) throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
    }
    internal async Task<JsonElement> AuthorizeList(string id, string reportId, string snapshotId, string snapshotHash,
        string actor, string session, CancellationToken ct, bool includeLineage = false)
    {
        var row = await Owned(id, actor, session, ct); var request = Request(row);
        if (includeLineage)
            AggregateCommandService.Allow(AggregateAction.ReadLineage,
                await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(request.Context, actor, session, ct));
        if (row["state"] != "COMPLETED" || (request.Kind != "CONFIG" && AggregateTargetIdentity.Id(request.Context) != reportId))
            throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
        if (row["stamp"].AsString != await Stamp(request, actor, session, ct)) throw new AggregatePreviewException("AGG_INPUT_STALE");
        using var result = JsonDocument.Parse(row["result"].AsString);
        bool Contains(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) return node.EnumerateArray().Any(Contains);
            if (node.ValueKind != JsonValueKind.Object) return false;
            if (node.TryGetProperty("kind", out var kind) && AggregateListWire.Supported(kind.GetString()))
                return node.GetProperty("id").GetString() == snapshotId && node.GetProperty("hash").GetString() == snapshotHash;
            return node.EnumerateObject().Any(p => Contains(p.Value));
        }
        var allowed = request.Kind == "CONFIG" ? result.RootElement.GetProperty("plan").GetProperty("previews").EnumerateObject().Any(p =>
            AggregateTargetIdentity.Id(p.Value.GetProperty("preview").GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!) == reportId && Contains(p.Value)) : Contains(result.RootElement);
        if (!allowed) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
          return result.RootElement.Clone();
    }
    internal async Task Cancel(string id, string actor, string session, CancellationToken ct)
    {
        var row = await Owned(id, actor, session, ct);
        await Stamp(Request(row), actor, session, ct);
        await Rows.UpdateOneAsync(new BsonDocument { ["_id"] = id, ["state"] = new BsonDocument("$in", new BsonArray { "QUEUED", "RUNNING" }) },
            new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "CANCELLED", ["updatedAt"] = DateTime.UtcNow },
                ["$unset"] = new BsonDocument { ["progress"] = "", ["result"] = "", ["errorPath"] = "", ["errorLocation"] = "", ["lease"] = "" } }, cancellationToken: ct);
    }
    internal async Task<string?> Current(AggregatePeriodContextDto context, string actor, string session, CancellationToken ct, bool completedOnly = false)
    {
        var authority = await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(context, actor, session, ct);
        AggregateCommandService.Allow(AggregateAction.EditMapping, authority);
        var filter = new BsonDocument { ["actor"] = actor, ["session"] = session, ["target"] = AggregateTargetIdentity.Id(context)!,
            ["kind"] = new BsonDocument("$ne", "FORMULA"),
            ["state"] = completedOnly ? new BsonString("COMPLETED") : new BsonDocument("$in", new BsonArray { "QUEUED", "RUNNING", "COMPLETED", "FAILED" }) };
        if (!completedOnly) filter["archived"] = new BsonDocument("$ne", true);
        var rows = await Rows.Find(filter).Sort(new BsonDocument(completedOnly ? "updatedAt" : "createdAt", -1)).Limit(completedOnly ? 20 : 1).ToListAsync(ct);
        foreach (var row in rows)
            try { await Read(row["_id"].AsString, actor, session, ct); return row["_id"].AsString; }
            catch (AggregatePreviewException) { } // A stale/revoked artifact is not a read entitlement.
        return null;
    }
    internal async Task Enqueue(string id, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var row = await Rows.Find(new BsonDocument("_id", id)).SingleAsync(ct);
        if (row["state"] != "QUEUED" && !(row["state"] == "RUNNING" && row["leaseUntil"].ToUniversalTime() < DateTime.UtcNow)) return;
        // Duplicate delivery is harmless: the worker claims a lease by atomic compare/update.
        // A short dispatch lease also recovers an interrupted enqueue on the next Start/read.
        var dispatch = await Rows.UpdateOneAsync(new BsonDocument { ["_id"] = id, ["$or"] = new BsonArray {
            new BsonDocument("dispatchUntil", new BsonDocument("$exists", false)), new BsonDocument("dispatchUntil", new BsonDocument("$lt", DateTime.UtcNow)) } },
            new BsonDocument("$set", new BsonDocument("dispatchUntil", DateTime.UtcNow.AddSeconds(30))), cancellationToken: ct);
        if (dispatch.ModifiedCount == 0) return;
        try { jobs.Enqueue<AggregatePreviewJobWorker>(w => w.Run(id, CancellationToken.None)); }
        catch { await Rows.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$unset", new BsonDocument("dispatchUntil", "")), cancellationToken: ct); throw; }
    }
    internal async Task Run(string id, IDynamicFlowDefinitionTransactionRunner transactions, IWorkReportPayloadWriter writer, CancellationToken cancellation)
    {
        if (!AggregateIntegrationGate.V2Ready(config)) return;
        var lease = Guid.NewGuid().ToString("N");
        var row = await Rows.FindOneAndUpdateAsync(new BsonDocument { ["_id"] = id, ["$or"] = new BsonArray {
                new BsonDocument("state", "QUEUED"), new BsonDocument { ["state"] = "RUNNING", ["leaseUntil"] = new BsonDocument("$lt", DateTime.UtcNow) } } },
            new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "RUNNING", ["lease"] = lease,
                    ["leaseUntil"] = DateTime.UtcNow.AddMinutes(6), ["updatedAt"] = DateTime.UtcNow }, ["$inc"] = new BsonDocument("attempt", 1),
                ["$unset"] = new BsonDocument { ["progress"] = "", ["result"] = "", ["error"] = "", ["errorPath"] = "", ["errorLocation"] = "" } },
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellation);
        if (row == null) return;
        var actor = row["actor"].AsString; var session = row["session"].AsString; var request = Request(row);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var owned = new BsonDocument { ["_id"] = id, ["lease"] = lease, ["state"] = "RUNNING" };
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = ObserveCancellation();
        async Task ObserveCancellation()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, monitorStop.Token);
                    if (!await Rows.Find(owned).AnyAsync(monitorStop.Token)) { timeout.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch { timeout.Cancel(); } // Lost ownership cannot authorize continued work.
        }
        async Task Progress(AggregatePreviewProgress progress)
        {
            if (!AggregateIntegrationGate.V2Ready(config) || row["stamp"].AsString != await Stamp(request, actor, session, ct))
                throw new AggregatePreviewException("AGG_INPUT_STALE");
            var json = JsonSerializer.Serialize(progress, AggregateCanonical.Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 8_000_000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            var saved = await Rows.UpdateOneAsync(owned, new BsonDocument("$set", new BsonDocument {
                ["progress"] = json, ["updatedAt"] = DateTime.UtcNow }), cancellationToken: ct);
            if (saved.MatchedCount != 1) throw new OperationCanceledException("Preview cancelled or lease replaced");
        }
        try
        {
            if (row["stamp"].AsString != await Stamp(request, actor, session, ct)) throw new AggregatePreviewException("AGG_INPUT_STALE");
            object result;
            if (request.Kind == "FORMULA")
            {
                var reader = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), true);
                var captured = await reader.ReadContextAsync(request.Context, actor, ct);
                // Stamp has checked persisted config/instance revisions; native context owns payload/lifecycle pins.
                captured = captured with { Revisions = captured.Revisions with { InstanceRevision = request.Recipe!.Expected.InstanceRevision, ConfigRevision = request.Recipe.Expected.ConfigRevision } };
                result = await new AggregatePreviewService(new AggregateMongoCommandReader.PinnedReader(reader,captured), new AggregateContentTableStore(db), new AggregateListStore(db))
                    .PreviewAsync(request.Recipe!, actor, ct, Progress, request.FormulaNodeId);
            }
            else if (request.Kind == "RECIPE")
                result = await new AggregatePreviewService(new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), true), new AggregateContentTableStore(db), new AggregateListStore(db))
                    .PreviewAsync(request.Recipe!, actor, ct, Progress, request.FormulaNodeId);
            else
            {
                var key = Convert.FromBase64String(config["AggregateMapping:ConfirmationKeyBase64"] ?? "");
                var service = new AggregateCommandService(new AggregateMongoStore(db, transactions, payloads, writer),
                    new AggregateMongoCommandReader(db, payloads, true, Progress), new(key), () => DateTimeOffset.UtcNow);
                if (request.Kind == "CONFIG")
                    result = await service.PreviewConfigAsync(new(id, "CONFIG_REVISION", request.ConfigId!, actor, session, DateTimeOffset.UtcNow), request.Context,
                        request.ConfigId!, request.Impact!, ct);
                else
                {
                    var change = request.Change!;
                    result = await service.PreviewChangeAsync(new(id, "APPLY", request.InstanceId!, actor, session, DateTimeOffset.UtcNow), request.Context,
                        request.InstanceId!, new(change.ExpectedRevision, change.Overrides, change.Selection, change.UnlinkedMembers, change.ResetToPinned), ct);
                }
            }
            if (!AggregateIntegrationGate.V2Ready(config) || row["stamp"].AsString != await Stamp(request, actor, session, ct))
                throw new AggregatePreviewException("AGG_INPUT_STALE");
            var json = JsonSerializer.Serialize(result, AggregateCanonical.Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 8_000_000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            await Rows.UpdateOneAsync(owned, new BsonDocument { ["$set"] = new BsonDocument {
                ["result"] = json, ["state"] = "COMPLETED", ["updatedAt"] = DateTime.UtcNow },
                ["$unset"] = new BsonDocument { ["progress"] = "", ["lease"] = "" } }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Do not retain partial values on failure/revocation. Cancellation cannot overwrite a newer lease.
            var code = ex is AggregatePreviewException aggregate ? aggregate.Code : ex is OperationCanceledException ? "AGG_PREVIEW_CANCELLED" : "AGG_PREVIEW_FAILED";
            var path = ex is AggregatePreviewException located ? located.Path : null;
            var location = ex is AggregatePreviewException failure ? failure.Location : null;
            await Rows.UpdateOneAsync(owned, new BsonDocument { ["$set"] = new BsonDocument {
                ["state"] = "FAILED", ["error"] = code, ["errorPath"] = path == null ? BsonNull.Value : new BsonString(path),
                ["errorLocation"] = location == null ? BsonNull.Value : new BsonString(JsonSerializer.Serialize(location, AggregateCanonical.Json)), ["updatedAt"] = DateTime.UtcNow },
                ["$unset"] = new BsonDocument { ["progress"] = "", ["result"] = "", ["lease"] = "" } }, cancellationToken: CancellationToken.None);
            if (cancellation.IsCancellationRequested) throw;
        }
        finally { monitorStop.Cancel(); await monitor; }
    }
    private Task<BsonDocument> Owned(string id, string actor, string session, CancellationToken ct)
        => LoadOwned(id, actor, session, ct);
    private async Task<BsonDocument> LoadOwned(string id, string actor, string session, CancellationToken ct)
        => await Rows.Find(new BsonDocument { ["_id"] = id, ["actor"] = actor, ["session"] = session }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
    private static AggregatePreviewJobRequest Request(BsonDocument row)
        => JsonSerializer.Deserialize<AggregatePreviewJobRequest>(row["request"].AsString, AggregateCanonical.Json)!;
    private static void Validate(AggregatePreviewJobRequest request)
    {
        if (request.Kind == "FORMULA" && request.Recipe != null && !string.IsNullOrWhiteSpace(request.FormulaNodeId)
            && request.Change == null && request.InstanceId == null && request.ConfigId == null && request.Impact == null && request.Recipe.Context == request.Context)
        { AggregatePreviewService.ValidateProbeSelection(request.Recipe); return; }
        if (request.FormulaNodeId != null) throw new AggregatePreviewException("AGG_JOB_REQUEST_INVALID");
        if (request.Kind == "RECIPE" && request.Recipe != null && request.Change == null && request.InstanceId == null && request.ConfigId == null && request.Impact == null && request.Recipe.Context == request.Context) return;
        if (request.Kind == "MAPPING" && request.Change != null && request.Recipe == null && request.ConfigId == null && request.Impact == null && !string.IsNullOrEmpty(request.InstanceId)) return;
        if (request.Kind == "CONFIG" && request.Impact != null && request.Recipe == null && request.Change == null && request.InstanceId == null && !string.IsNullOrEmpty(request.ConfigId)
            && request.Impact.MigrateInstances.Count <= 100) return;
        throw new AggregatePreviewException("AGG_JOB_REQUEST_INVALID");
    }
    private async Task<string> Stamp(AggregatePreviewJobRequest request, string actor, string session, CancellationToken ct)
    {
        var authority = await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(request.Context, actor, session, ct);
        AggregateCommandService.Allow(AggregateAction.EditMapping, authority);
        if (request.Kind == "FORMULA") { Validate(request); AggregateCommandService.Allow(AggregateAction.ReadLineage, authority); }
        if (AggregatePeriodContextContract.Compare(request.Context, authority.Read.Context) is { } issue) throw new AggregatePreviewException(issue.Code);
        AggregateRecipeDto recipe; string? instanceStamp = null;
        if (request.Kind == "CONFIG")
        {
            AggregateCommandService.Allow(AggregateAction.EditConfig, authority);
            var headRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Configs).Find(new BsonDocument("_id", request.ConfigId)).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var head = AggregateMongoTransaction.Read<AggregateConfigHead>(headRow).Value;
            AggregateCommandService.Scope(head, authority);
            if (head.HeadRevision != request.Impact!.ExpectedHeadRevision) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            recipe = AggregateOverlay.Validate(request.Impact.Recipe);
            var selected = new List<string> { headRow["body"].AsString };
            foreach (var pin in request.Impact.MigrateInstances.OrderBy(p => p.InstanceId, StringComparer.Ordinal))
            {
                var row = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id", pin.InstanceId)).FirstOrDefaultAsync(ct)
                    ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
                var instance = AggregateMongoTransaction.Read<AggregateInstanceState>(row).Value;
                if (instance.ConfigId != request.ConfigId || instance.Revision != pin.ExpectedRevision || instance.State == "FROZEN") throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
                var current = await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(instance.Context, actor, session, ct);
                AggregateCommandService.Allow(AggregateAction.ApplyDraft, current);
                var raw = new AggregatePreviewRequestDto(current.Read.Context, instance.Id, instance.ConfigId, current.Read.Revisions, recipe, instance.Selection);
                selected.Add(row["body"].AsString);
                selected.Add(await Stamp(new("RECIPE", current.Read.Context, raw, null, null), actor, session, ct));
            }
            instanceStamp = AggregateCanonical.Hash(selected);
        }
        else if (request.Kind == "FORMULA")
        {
            var expected = request.Recipe!.Expected;
            var instanceRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
                .Find(new BsonDocument("_id", AggregateCommandService.InstanceKey(authority.Read.Context))).FirstOrDefaultAsync(ct);
            var headRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Configs)
                .Find(new BsonDocument("_id", AggregateCanonical.Key(request.Context.WorkId,request.Context.AssignmentId,request.Context.BindingId))).FirstOrDefaultAsync(ct);
            var instance = instanceRow == null ? null : AggregateMongoTransaction.Read<AggregateInstanceState>(instanceRow).Value;
            var head = headRow == null ? null : AggregateMongoTransaction.Read<AggregateConfigHead>(headRow).Value;
            if(expected.InstanceRevision != (instance?.Revision ?? 0) || expected.ConfigRevision != (instance?.ConfigRevision ?? head?.HeadRevision ?? 0)
                || instance != null && request.Recipe.InstanceId != instance.Id || head != null && request.Recipe.ConfigId != head.Id)
                throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            instanceStamp = AggregateCanonical.Hash(new { Instance = instanceRow?.GetValue("body").AsString, Head = headRow?.GetValue("body").AsString });
            var parsed = AggregateMappingValidator.Parse(JsonSerializer.Serialize(request.Recipe!.Recipe, AggregateCanonical.Json), request.FormulaNodeId);
            if (!parsed.StructurallyValid) throw new AggregatePreviewException(parsed.Issues[0].Code, parsed.Issues[0].Path);
            recipe = parsed.Recipe!;
        }
        else if (request.Kind == "RECIPE") recipe = AggregateOverlay.Validate(request.Recipe!.Recipe);
        else
        {
            var instanceRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id", request.InstanceId)).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var instance = AggregateMongoTransaction.Read<AggregateInstanceState>(instanceRow).Value;
            AggregateCommandService.Draft(instance, authority, request.Change!.ExpectedRevision);
            var versionRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Versions).Find(new BsonDocument("_id", AggregateCommandService.VersionKey(instance.ConfigId, instance.ConfigRevision))).SingleAsync(ct);
            var version = AggregateMongoTransaction.Read<AggregateConfigVersion>(versionRow).Value;
            recipe = AggregateCommandService.MappingCandidate(instance, version,
                new(request.Change.ExpectedRevision, request.Change.Overrides, request.Change.Selection,
                    request.Change.UnlinkedMembers, request.Change.ResetToPinned), authority.Read.Context).Recipe;
            instanceStamp = AggregateCanonical.Hash(new { Instance = instanceRow["body"].AsString, Version = versionRow["body"].AsString });
        }
        var reader = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), true);
        var sources = new List<object>();
        var selection = request.Recipe?.Selection ?? request.Change?.Selection;
        foreach (var form in recipe.Nodes.Where(n => n.Kind == "SOURCE").Select(n => n.Form!).Distinct())
        {
            var schema = await reader.ReadSchemaAsync(form, authority.Read, actor, ct);
            var reportSet = recipe.Nodes.Where(n => n.Kind == "SOURCE" && n.Form == form).SelectMany(n => n.Outputs)
                .All(p => recipe.TimeRules.Single(r => r.Id == p.TimeRuleId).Mode == AggregateReportSetFilter.Mode);
            var listing = reportSet ? await reader.ListReportSetSourcesAsync(authority.Read, form, actor, ct)
                : await reader.ListSourcesAsync(authority.Read, form, actor, ct);
            var nodes = recipe.Nodes.Where(n => n.Kind == "SOURCE" && n.Form == form).Select(n => n.Id).ToHashSet();
            var currentIds = listing.Headers.Select(h => h.Pin.ReportId).ToHashSet();
            if (request.Kind == "FORMULA" && selection!.Sources.Where(s=>nodes.Contains(s.SourceNodeId)).SelectMany(s=>s.ReportIds)
                .Any(id=>!listing.Headers.Any(h=>h.Pin.ReportId==id && h.Active && !h.Deleted && h.Pin.IsCurrent && h.Pin.Status=="Approved" && h.WholeReportReadable)))
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            var absent = selection?.Sources.Where(s => nodes.Contains(s.SourceNodeId)).SelectMany(s => s.ReportIds.Concat(s.ExcludedReportIds))
                .Where(id => !currentIds.Contains(id)).Distinct().ToArray() ?? [];
            var inactive = absent.Length == 0 ? [] : await reader.ReadInactiveSourcesAsync(authority.Read, form, absent, actor, ct);
            if (absent.Any(id => !inactive.Any(h => h.Pin.ReportId == id))) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            if (!listing.Complete || listing.Headers.Any(h => !h.WholeReportReadable) || listing.Slots.Any(s => !s.Readable))
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            sources.Add(new { schema, listing.MembershipRevision, inactive });
        }
        return AggregateCanonical.Hash(new { authority.Read.Context, authority.Read.Revisions, authority.Read.AuthorizationFingerprint,
            authority.Pins, instanceStamp, sources, dateFilters = AggregatePartialDate.FilterSemantics });
    }
}

// Uses the application's existing Hangfire executor. No additional scheduler or hosted service.
public sealed class AggregatePreviewJobWorker(MongoDbContext db, IWorkReportPayloadReader reader,
    IWorkReportPayloadWriter writer, IDynamicFlowDefinitionTransactionRunner transactions, IConfiguration configuration)
{
    [AutomaticRetry(Attempts = 0)] // explicit retry restarts a failed attempt; expired lease recovery covers process loss.
    public Task Run(string id, CancellationToken ct) => new AggregatePreviewJobs(db, reader, configuration).Run(id, transactions, writer, ct);
}
