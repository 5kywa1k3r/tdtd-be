using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Coordinates physical collection with capture/upload and transactional publication.
// Expiry recovers a crashed worker; an expired owner is never allowed to publish/delete.
internal sealed class AggregateContentScopeGate(MongoDbContext db, string scope, string owner) : IAsyncDisposable
{
    internal const string Collection = "aggregate_content_scope_gates";
    private IMongoCollection<BsonDocument> Rows => db.Db.GetCollection<BsonDocument>(Collection);
    private static BsonDocument Available(string scope) => new() { ["_id"] = scope, ["$or"] = new BsonArray {
        new BsonDocument("owner", new BsonDocument("$exists", false)),
        new BsonDocument("until", new BsonDocument("$lt", DateTime.UtcNow)) } };

    internal static async Task<AggregateContentScopeGate?> Acquire(MongoDbContext db, string scope, CancellationToken ct, bool wait = true)
    {
        var rows = db.Db.GetCollection<BsonDocument>(Collection);
        try { await rows.UpdateOneAsync(new BsonDocument("_id", scope), new BsonDocument("$setOnInsert", new BsonDocument("fence", 0L)), new UpdateOptions { IsUpsert = true }, ct); }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        var owner = Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < (wait ? 40 : 1); attempt++)
        {
            var row = await rows.FindOneAndUpdateAsync(Available(scope), new BsonDocument { ["$set"] = new BsonDocument {
                ["owner"] = owner, ["until"] = DateTime.UtcNow.AddMinutes(30) }, ["$inc"] = new BsonDocument("fence", 1L) },
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
            if (row != null) return new(db, scope, owner);
            if (wait) await Task.Delay(250, ct);
        }
        return null;
    }
    internal async Task Ensure(CancellationToken ct)
    {
        var renewed = await Rows.UpdateOneAsync(new BsonDocument { ["_id"] = scope, ["owner"] = owner,
            ["until"] = new BsonDocument("$gt", DateTime.UtcNow) },
            new BsonDocument("$set", new BsonDocument("until", DateTime.UtcNow.AddMinutes(30))), cancellationToken: ct);
        if (renewed.MatchedCount != 1) throw new AggregatePreviewException("AGG_INPUT_STALE");
    }
    internal static async Task Fence(MongoDbContext db, IClientSessionHandle session, string scope, CancellationToken ct)
    {
        // The write conflicts with a collector acquiring this same document until the
        // report/view transaction commits. No reference can publish during collection.
        try
        {
            var result = await db.Db.GetCollection<BsonDocument>(Collection).UpdateOneAsync(session, Available(scope),
                new BsonDocument("$inc", new BsonDocument("fence", 1L)), new UpdateOptions { IsUpsert = true }, ct);
            if (result.MatchedCount == 0 && result.UpsertedId == null) throw new AggregatePreviewException("AGG_INPUT_STALE");
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { throw new AggregatePreviewException("AGG_INPUT_STALE"); }
    }
    public async ValueTask DisposeAsync()
    {
        await Rows.UpdateOneAsync(new BsonDocument { ["_id"] = scope, ["owner"] = owner },
            new BsonDocument("$unset", new BsonDocument { ["owner"] = "", ["until"] = "" }), cancellationToken: CancellationToken.None);
    }
}
