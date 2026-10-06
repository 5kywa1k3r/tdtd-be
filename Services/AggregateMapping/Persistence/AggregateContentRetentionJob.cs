using System.Text.Json;
using Hangfire;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Only derived content artifacts are collected. Frozen/current references are roots,
// not historical versions that may be evicted. Each save durably requests another pass.
public sealed class AggregateContentRetentionJob(MongoDbContext db, IMinioClient minio, IConfiguration configuration)
{
    internal const int KeepVersions = 3;
    internal const string Intents = "aggregate_content_cleanup_intents";
    private IMongoCollection<BsonDocument> Rows(string name) => db.Db.GetCollection<BsonDocument>(name);

    internal static async Task PlanOnSave(MongoDbContext db, IClientSessionHandle session, string target, string workId, CancellationToken ct)
    {
        var snapshots = await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots)
            .Find(session, new BsonDocument("target", target)).Project(new BsonDocument { ["version"] = 1, ["body"] = 1 }).Limit(10001).ToListAsync(ct);
        if (snapshots.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var excess = snapshots.Select(r => AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value)
            .GroupBy(r => r.TableId).Sum(g => Math.Max(0, g.Count() - KeepVersions));
        await db.Db.GetCollection<BsonDocument>(Intents).UpdateOneAsync(session, new BsonDocument("_id", target),
            new BsonDocument { ["$set"] = new BsonDocument { ["workId"] = workId, ["state"] = "PENDING",
                ["excessAtSave"] = excess, ["updatedAt"] = DateTime.UtcNow }, ["$inc"] = new BsonDocument("generation", 1L) },
            new UpdateOptions { IsUpsert = true }, ct);
    }

    internal static async Task Schedule(MongoDbContext db, IBackgroundJobClient? jobs, string target, CancellationToken ct)
    {
        if (jobs == null) return;
        var rows = db.Db.GetCollection<BsonDocument>(Intents);
        var claim = await rows.FindOneAndUpdateAsync(new BsonDocument { ["_id"] = target, ["state"] = "PENDING", ["$or"] = new BsonArray {
            new BsonDocument("dispatchUntil", new BsonDocument("$exists", false)),
            new BsonDocument("dispatchUntil", new BsonDocument("$lt", DateTime.UtcNow)) } },
            new BsonDocument("$set", new BsonDocument("dispatchUntil", DateTime.UtcNow.AddMinutes(10))),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (claim == null) return;
        try { jobs.Enqueue<AggregateContentRetentionJob>(w => w.Run(target, CancellationToken.None)); }
        catch { await rows.UpdateOneAsync(new BsonDocument { ["_id"] = target, ["generation"] = claim["generation"] },
            new BsonDocument("$unset", new BsonDocument("dispatchUntil", "")), cancellationToken: ct); }
    }

    [AutomaticRetry(Attempts = 0)] // Another save/read re-enqueues the durable pending intent.
    public async Task Run(string target, CancellationToken ct)
    {
        if (!AggregateIntegrationGate.V2Ready(configuration)) return;
        await using var gate = await AggregateContentScopeGate.Acquire(db, target, ct, wait: false);
        if (gate == null) { await RetryLater(target, ct); return; }
        var plan = await Rows(Intents).Find(new BsonDocument { ["_id"] = target, ["state"] = "PENDING" }).FirstOrDefaultAsync(ct);
        if (plan == null) return;
        try
        {
            var roots = await RootReferences(target, plan["workId"].AsString, ct);
            var documents = await Rows(AggregateCollections.ContentSnapshots).Find(new BsonDocument("target", target)).Limit(10001).ToListAsync(ct);
            if (documents.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            var snapshots = documents.Select(r => (Row: r, Intent: AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value)).ToArray();
            var keep = snapshots.GroupBy(x => x.Intent.TableId).SelectMany(g => g.OrderByDescending(x => x.Intent.PayloadRevision)
                .ThenBy(x => x.Row["_id"].AsString, StringComparer.Ordinal).Take(KeepVersions)).Select(x => x.Row["_id"].AsString).ToHashSet(StringComparer.Ordinal);
            // One retained archive for each pinned manifest suffices; multiple identical
            // payload revisions do not need duplicate files to preserve a pinned result.
            foreach (var group in snapshots.Where(x => roots.Contains(x.Intent.Reference.Id)).GroupBy(x => (x.Intent.TableId, x.Intent.Reference.Id)))
                keep.Add(group.OrderByDescending(x => x.Intent.PayloadRevision).First().Row["_id"].AsString);
            foreach (var snapshot in snapshots.Where(x => !keep.Contains(x.Row["_id"].AsString)))
            {
                await gate.Ensure(ct);
                var intent = snapshot.Intent;
                var objectKey = snapshot.Row.GetValue("objectKey", $"aggregate-content/{target}/{intent.PayloadRevision}/{intent.TableId}/{intent.Reference.Hash}.ndjson").AsString;
                var bucket = snapshot.Row.GetValue("bucket", configuration["Uploads:Bucket"] ?? "tdtd-attachments").AsString;
                // Never accept an arbitrary object key/bucket from a corrupt intent.
                if (bucket != (configuration["Uploads:Bucket"] ?? "tdtd-attachments") || objectKey != $"aggregate-content/{target}/{intent.PayloadRevision}/{intent.TableId}/{intent.Reference.Hash}.ndjson")
                    throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(objectKey), ct);
                await gate.Ensure(ct);
                await Rows(AggregateCollections.ContentSnapshots).DeleteOneAsync(new BsonDocument {
                    ["_id"] = snapshot.Row["_id"], ["body"] = snapshot.Row["body"], ["target"] = target }, ct);
            }
            var retained = await Rows(AggregateCollections.ContentSnapshots).Find(new BsonDocument("target", target)).ToListAsync(ct);
            foreach (var row in retained) roots.Add(AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(row).Value.Reference.Id);
            // Running previews may hold unpublished intermediate handles. Defer their
            // physical collection; the save generation stays pending for the next pass.
            var busy = await Rows(AggregatePreviewJobs.Collection).Find(new BsonDocument { ["workId"] = plan["workId"],
                ["state"] = new BsonDocument("$in", new BsonArray { "QUEUED", "RUNNING" }) }).AnyAsync(ct);
            if (busy) { await RetryLater(target, ct); return; }
            await Collect(target, roots, gate, ct);
            await Rows(Intents).UpdateOneAsync(new BsonDocument { ["_id"] = target, ["generation"] = plan["generation"] },
                new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "COMPLETED", ["completedAt"] = DateTime.UtcNow },
                    ["$unset"] = new BsonDocument { ["dispatchUntil"] = "", ["error"] = "" } }, cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Rows(Intents).UpdateOneAsync(new BsonDocument { ["_id"] = target, ["generation"] = plan["generation"] },
                new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "PENDING", ["error"] = e is AggregatePreviewException a ? a.Code : "AGG_CONTENT_CLEANUP_FAILED" },
                    ["$unset"] = new BsonDocument("dispatchUntil", "") }, cancellationToken: ct);
            throw;
        }
    }

    private Task RetryLater(string target, CancellationToken ct) => Rows(Intents).UpdateOneAsync(new BsonDocument("_id", target),
        new BsonDocument("$unset", new BsonDocument("dispatchUntil", "")), cancellationToken: ct);

    private async Task<HashSet<string>> RootReferences(string target, string workId, CancellationToken ct)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in new[] { AggregateCollections.Instances, AggregateCollections.Frozen, AggregateCollections.NativeContent })
        {
            var rows = await Rows(collection).Find(new BsonDocument("target", target)).Project(new BsonDocument("body", 1)).Limit(1001).ToListAsync(ct);
            if (rows.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            foreach (var row in rows) AddReferences(row["body"].AsString, roots);
        }
        // Config preview jobs can contain other reports in the same work. Do not
        // mistake their top-level target for the scope of every result they contain.
        var jobs = await Rows(AggregatePreviewJobs.Collection).Find(new BsonDocument { ["workId"] = workId, ["state"] = "COMPLETED" })
            .Project(new BsonDocument("result", 1)).Limit(1001).ToListAsync(ct);
        if (jobs.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        foreach (var job in jobs) if (job.TryGetValue("result", out var result) && result.IsString) AddReferences(result.AsString, roots);
        return roots;
    }
    private static void AddReferences(string json, HashSet<string> roots)
    {
        using var document = JsonDocument.Parse(json);
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) Visit(item); return; }
            if (value.ValueKind != JsonValueKind.Object) return;
            if (value.TryGetProperty("kind", out var kind) && kind.GetString() == AggregateContentTableStore.Kind
                && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) roots.Add(id.GetString()!);
            foreach (var property in value.EnumerateObject()) Visit(property.Value);
        }
        Visit(document.RootElement);
    }
    private async Task Collect(string scope, HashSet<string> roots, AggregateContentScopeGate gate, CancellationToken ct)
    {
        // Metadata only: never materialize text in the cleanup job. Budget failures
        // happen before deletion and keep the durable intent pending.
        var manifests = await Rows(AggregateContentTableStore.Manifests).Find(new BsonDocument("scope", scope)).Limit(10001).ToListAsync(ct);
        if (manifests.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var keptRows = manifests.Where(m => roots.Contains(m["_id"].AsString)).SelectMany(m => m["rowIds"].AsBsonArray.Select(x => x.AsString)).ToHashSet(StringComparer.Ordinal);
        if (keptRows.Count > 250000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var records = await Rows(AggregateContentTableStore.Rows).Find(new BsonDocument("scope", scope)).Project(new BsonDocument { ["parts"] = 1 }).Limit(250001).ToListAsync(ct);
        if (records.Count > 250000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var keptParts = records.Where(r => keptRows.Contains(r["_id"].AsString)).SelectMany(r => r["parts"].AsBsonArray.Select(x => x.AsString)).ToHashSet(StringComparer.Ordinal);
        if (keptParts.Count > 250000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var parts = await Rows(AggregateContentTableStore.Parts).Find(new BsonDocument("scope", scope)).Project(new BsonDocument("_id", 1)).Limit(250001).ToListAsync(ct);
        if (parts.Count > 250000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        foreach (var group in new[] {
            (Name: AggregateContentTableStore.Manifests, Ids: manifests.Where(m => !roots.Contains(m["_id"].AsString)).Select(m => m["_id"].AsString)),
            (Name: AggregateContentTableStore.Rows, Ids: records.Where(r => !keptRows.Contains(r["_id"].AsString)).Select(r => r["_id"].AsString)),
            (Name: AggregateContentTableStore.Parts, Ids: parts.Where(p => !keptParts.Contains(p["_id"].AsString)).Select(p => p["_id"].AsString)) })
            foreach (var batch in group.Ids.Chunk(128))
            {
                await gate.Ensure(ct);
                await Rows(group.Name).DeleteManyAsync(new BsonDocument { ["scope"] = scope, ["_id"] = new BsonDocument("$in", new BsonArray(batch)) }, ct);
            }
    }
}
