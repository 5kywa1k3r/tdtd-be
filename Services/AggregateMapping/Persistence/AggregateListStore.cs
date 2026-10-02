using tdtd_be.DTOs.AggregateMapping;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Immutable derived artifacts. Native report records remain the only editable payload.
internal sealed class AggregateListStore(MongoDbContext db) : IAggregateListSink
{
    internal const string Manifests = "aggregate_list_manifests", Rows = "aggregate_list_rows";
    private IMongoCollection<BsonDocument> Collection(string name) => db.Db.GetCollection<BsonDocument>(name);
    public async IAsyncEnumerable<AggregateListRow> ReadRowsAsync(AggregateReadContext context, AggregateListReference reference,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var scope = AggregateTargetIdentity.Id(context.Context) ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var manifest = await Manifest(scope, reference.Id, reference.Hash, ct);
        foreach (var chunk in manifest["rowIds"].AsBsonArray.Chunk(20))
        {
            var rows = await Collection(Rows).Find(new BsonDocument { ["scope"] = scope, ["_id"] = new BsonDocument("$in", new BsonArray(chunk)) })
                .Project(new BsonDocument { ["_id"] = 1, ["body"] = 1 }).ToListAsync(ct);
            foreach (var id in chunk)
            {
                ct.ThrowIfCancellationRequested();
                var body = (rows.SingleOrDefault(r => r["_id"] == id) ?? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE"))["body"].AsString;
                if (AggregateCanonical.Hash(new { scope, body }) != id.AsString) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
                using var doc = JsonDocument.Parse(body);
                yield return AggregateListWire.DecodeRow(doc.RootElement);
            }
        }
    }
    public async Task<AggregateListReference> CaptureAsync(AggregateReadContext context, AggregateListValue value, CancellationToken ct)
    {
        var scope = AggregateTargetIdentity.Id(context.Context) ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        if (value.Records.Count > 40_000 || value.Records.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != value.Records.Count)
            throw new AggregatePreviewException("AGG_LIST_ID_COLLISION");
        var ids = new List<string>(); var pending = new List<WriteModel<BsonDocument>>();
        foreach (var record in value.Records)
        {
            var body = JsonSerializer.Serialize(AggregateListWire.EncodeRow(record), AggregateCanonical.Json);
            var id = AggregateCanonical.Hash(new { scope, body }); ids.Add(id);
            var preview = JsonNode.Parse(body)!.AsObject(); preview.Remove("origin");
            foreach (var cell in preview["cells"]!.AsObject().Select(p => p.Value!.AsObject()))
            {
                cell.Remove("lineage"); cell.Remove("exact");
                if (cell["type"]!.GetValue<string>() == "TEXT" && cell["value"] is JsonValue textNode)
                {
                    var text = textNode.GetValue<string>(); var length = text.Length;
                    var take = Math.Min(240, length); if (take > 0 && char.IsHighSurrogate(text[take - 1])) take--;
                    cell["value"] = text[..take]; cell["characterCount"] = length; cell["hasMore"] = take < length;
                }
            }
            var document = new BsonDocument { ["_id"] = id, ["scope"] = scope, ["key"] = record.Key,
                ["body"] = body, ["preview"] = preview.ToJsonString() };
            // A compact, reproducible metadata index: no values, full text, or trace arrays.
            var metadata = Metadata(JsonSerializer.SerializeToElement(AggregateListWire.EncodeRow(record), AggregateCanonical.Json));
            pending.Add(new UpdateOneModel<BsonDocument>(new BsonDocument("_id", id), new BsonDocument {
                ["$setOnInsert"] = document, ["$set"] = new BsonDocument("metadata", metadata.GetRawText()) }) { IsUpsert = true });
            if (pending.Count == 64) { await Collection(Rows).BulkWriteAsync(pending, cancellationToken: ct); pending.Clear(); }
        }
        if (pending.Count > 0) await Collection(Rows).BulkWriteAsync(pending, cancellationToken: ct);
        var keys = value.Records.Select(r => r.Key).ToList();
        var hash = AggregateCanonical.Hash(new { value.Schema, ids, keys });
        var snapshotId = AggregateCanonical.Key(scope, hash);
        var manifest = new BsonDocument { ["_id"] = snapshotId, ["scope"] = scope, ["hash"] = hash,
            ["schema"] = JsonSerializer.Serialize(value.Schema, AggregateCanonical.Json), ["rowIds"] = new BsonArray(ids), ["keys"] = new BsonArray(keys), ["count"] = ids.Count };
        await Collection(Manifests).UpdateOneAsync(new BsonDocument("_id", snapshotId), new BsonDocument("$setOnInsert", manifest), new UpdateOptions { IsUpsert = true }, ct);
        return new(AggregateListWire.Kind, snapshotId, hash, ids.Count, value.Schema);
    }
    internal async Task<AggregateListValue> Load(string reportId, AggregateListReference reference, CancellationToken ct)
    {
        var manifest = await Manifest(reportId, reference.Id, reference.Hash, ct);
        if (reference.Kind != AggregateListWire.Kind || reference.Count != manifest["count"].AsInt32
            || AggregateCanonical.Hash(reference.Schema) != AggregateCanonical.Hash(JsonSerializer.Deserialize<AggregateListSchema>(manifest["schema"].AsString, AggregateCanonical.Json)))
            throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
        var rows = new List<AggregateListRow>();
        foreach (var chunk in manifest["rowIds"].AsBsonArray.Chunk(50))
        {
            var values = await Collection(Rows).Find(new BsonDocument { ["scope"] = reportId, ["_id"] = new BsonDocument("$in", new BsonArray(chunk)) }).ToListAsync(ct);
            foreach (var id in chunk)
            {
                var row = values.SingleOrDefault(r => r["_id"] == id) ?? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
                var body = row["body"].AsString;
                if (AggregateCanonical.Hash(new { scope = reportId, body }) != id.AsString) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
                using var doc = JsonDocument.Parse(body); rows.Add(AggregateListWire.DecodeRow(doc.RootElement));
            }
        }
        return new(reference.Schema, rows);
    }
    internal async Task<object> Page(string reportId, string id, string hash, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 50) throw new AggregatePreviewException("AGG_PAGE_SIZE_INVALID");
        var manifest = await Manifest(reportId, id, hash, ct);
        var count = manifest["count"].AsInt32;
        if (offset > count) throw new AggregatePreviewException("AGG_CURSOR_STALE");
        var ids = manifest["rowIds"].AsBsonArray.Skip(offset).Take(limit).ToArray();
        // Projection keeps all full text and provenance bodies out of page reads.
        var rows = await Collection(Rows).Find(new BsonDocument { ["scope"] = reportId, ["_id"] = new BsonDocument("$in", new BsonArray(ids)) })
            .Project(new BsonDocument { ["_id"] = 1, ["preview"] = 1, ["metadata"] = 1 }).ToListAsync(ct);
        // Compatibility for pre-index snapshots: read only missing rows on this page,
        // verify their immutable body hash; never scan/load the remaining snapshot.
        var missing = rows.Where(r => !r.Contains("metadata")).Select(r => r["_id"]).ToArray();
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Contains("metadata")))
            metadata[row["_id"].AsString] = JsonSerializer.Deserialize<JsonElement>(row["metadata"].AsString);
        if (missing.Length > 0)
            foreach (var row in await Collection(Rows).Find(new BsonDocument { ["scope"] = reportId, ["_id"] = new BsonDocument("$in", new BsonArray(missing)) })
                .Project(new BsonDocument { ["_id"] = 1, ["body"] = 1 }).ToListAsync(ct))
            {
                var body = row["body"].AsString;
                if (AggregateCanonical.Hash(new { scope = reportId, body }) != row["_id"].AsString) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
                using var doc = JsonDocument.Parse(body); metadata[row["_id"].AsString] = Metadata(doc.RootElement);
            }
        var page = ids.Select(rowId => JsonSerializer.Deserialize<JsonElement>((rows.SingleOrDefault(r => r["_id"] == rowId)
            ?? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE"))["preview"].AsString)).ToArray();
        return new { SnapshotId = id, SnapshotHash = hash, Total = count, Offset = offset,
            NextOffset = offset + page.Length < count ? (int?)(offset + page.Length) : null, Items = page,
            Metadata = ids.Select(rowId => metadata[rowId.AsString]).ToArray() };
    }
    internal async Task<object> Detail(string reportId, string id, string hash, string rowKey, string fieldId, CancellationToken ct)
    {
        var manifest = await Manifest(reportId, id, hash, ct);
        var index = manifest["keys"].AsBsonArray.IndexOf(new BsonString(rowKey));
        if (index < 0) throw new AggregatePreviewException("AGG_LIST_ITEM_UNAVAILABLE");
        var row = await Collection(Rows).Find(new BsonDocument { ["scope"] = reportId,
            ["_id"] = manifest["rowIds"].AsBsonArray[index] }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_LIST_ITEM_UNAVAILABLE");
        var body = row["body"].AsString;
        if (AggregateCanonical.Hash(new { scope = reportId, body }) != row["_id"].AsString)
            throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.GetProperty("cells").TryGetProperty(fieldId, out var cell)) throw new AggregatePreviewException("AGG_LIST_FIELD_REF");
        return new { SnapshotId = id, SnapshotHash = hash, RowKey = rowKey, FieldId = fieldId,
            Origin = doc.RootElement.GetProperty("origin").Clone(), Cell = cell.Clone(), Metadata = Metadata(doc.RootElement) };
    }
    private static JsonElement Metadata(JsonElement row) => JsonSerializer.SerializeToElement(new {
        Origin = row.GetProperty("origin").Clone(), Fields = row.GetProperty("cells").EnumerateObject().ToDictionary(c => c.Name,
            c => c.Value.GetProperty("lineage").EnumerateArray().Select(t => t.TryGetProperty("fieldId", out var id) ? id.GetString() : null)
                .Where(id => id != null).Distinct().ToArray()) }, AggregateCanonical.Json);
    private async Task<BsonDocument> Manifest(string reportId, string id, string hash, CancellationToken ct)
    {
        var row = await Collection(Manifests).Find(new BsonDocument { ["_id"] = id, ["scope"] = reportId, ["hash"] = hash }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
        var schema = JsonSerializer.Deserialize<AggregateListSchema>(row["schema"].AsString, AggregateCanonical.Json)!;
        var ids = row["rowIds"].AsBsonArray.Select(v => v.AsString).ToList();
        var keys = row["keys"].AsBsonArray.Select(v => v.AsString).ToList();
        if (row["count"].AsInt32 != ids.Count || keys.Count != ids.Count || AggregateCanonical.Hash(new { Schema = schema, ids, keys }) != hash)
            throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
        return row;
    }
}
