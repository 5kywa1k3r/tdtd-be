using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed partial class AggregateMongoCommandReader
{
    // Per-reader diagnostics for the runtime harness; no shared cache or API payload.
    internal int PinBatchQueryCount { get; private set; }
    internal async Task PinMany(List<AggregateAuthorityPin> pins,
        IEnumerable<(string Collection, string Id)> requested, CancellationToken ct)
    {
        var captured = pins.ToDictionary(p => (p.Collection, p.Id));
        foreach (var group in requested.Distinct().GroupBy(p => p.Collection).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var declarations = group.Key == AggregateCollections.Declarations;
            foreach (var chunk in group.Select(p => p.Id).Order(StringComparer.Ordinal).Chunk(256))
            {
                var keys = chunk.Select(id => declarations ? (BsonValue)new BsonString(id)
                    : ObjectId.TryParse(id, out var key) ? new BsonObjectId(key)
                    : throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE")).ToArray();
                var rows = await db.Db.GetCollection<BsonDocument>(group.Key)
                    .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(keys)))).ToListAsync(ct);
                PinBatchQueryCount++;
                var found = rows.ToDictionary(row => row["_id"]);
                for (var index = 0; index < chunk.Length; index++)
                {
                    var id = chunk[index];
                    captured.TryGetValue((group.Key, id), out var old);
                    if (!found.TryGetValue(keys[index], out var row))
                    {
                        if (old != null) throw new AggregatePreviewException("AGG_INPUT_STALE");
                        if (declarations) continue; // A declaration is optional, as in PinDeclaration.
                        throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
                    }
                    var pin = new AggregateAuthorityPin(group.Key, id, Fingerprint(row));
                    if (old != null && old != pin) throw new AggregatePreviewException("AGG_INPUT_STALE");
                    if (old == null) { pins.Add(pin); captured.Add((group.Key, id), pin); }
                }
            }
        }
    }
}
