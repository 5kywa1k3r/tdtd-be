using tdtd_be.DTOs.AggregateMapping;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateContentPageRow(string RowKey, string UnitName, string? ReportTitle,
    string OccurrenceKey, string? CompletedDate, string? SubmittedAtUtc, string State,
    string Sample, int CharacterCount, bool HasMore);
internal sealed record AggregateContentPage(IReadOnlyList<AggregateContentPageRow> Rows, int Total, int? NextOffset);
internal sealed record AggregateContentTextPart(string Text, int? NextPart, int CharacterCount);

// Immutable, scope-local records. IDs and hashes are integrity keys, never authority.
// Callers MUST authorize the referenced report/job and revalidate its pins before each read.
// No public lookup-by-hash route, TTL, or mutation of an existing sealed snapshot.
internal sealed class AggregateContentTableStore(MongoDbContext db) : IAggregateContentSink
{
    internal const string Manifests = "aggregate_content_manifests", Rows = "aggregate_content_rows", Parts = "aggregate_content_parts";
    internal const string Kind = "REPORT_CONTENT_TABLE_V1";
    private const int PartCharacters = 16_384, MaximumRows = 10_000;
    private bool _indexesReady;
    private IMongoCollection<BsonDocument> Collection(string name) => db.Db.GetCollection<BsonDocument>(name);
    internal async Task CreateIndexes(CancellationToken ct)
    {
        await Collection(Rows).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
            .Ascending("scope").Ascending("sourceKey").Ascending("rowKey"), new() { Name = "aggregate_content_source_row" }), cancellationToken: ct);
    }

    public Task<AggregateContentReference> CaptureAsync(AggregateReadContext context, AggregateTable table, CancellationToken ct)
        => Capture(AggregateTargetIdentity.Id(context.Context) ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE"), table, ct);
    internal async Task<AggregateContentReference> Capture(string scope, AggregateTable table, CancellationToken ct)
    {
        await using var gate = await AggregateContentScopeGate.Acquire(db, scope, ct)
            ?? throw new AggregatePreviewException("AGG_INPUT_STALE");
        return await CaptureCore(scope, table, gate, ct);
    }
    private async Task<AggregateContentReference> CaptureCore(string scope, AggregateTable table, AggregateContentScopeGate gate, CancellationToken ct)
    {
        if(!_indexesReady){await CreateIndexes(ct);_indexesReady=true;}
        if (string.IsNullOrWhiteSpace(scope) || table.ContentRows == null || table.ContentRows.Count != table.Rows.Count
            || table.Rows.Count > MaximumRows || table.Schema.Layout != "vertical" || table.Schema.Columns.Count != 2)
            throw new AggregatePreviewException("AGG_CONTENT_TABLE_INVALID");
        var ids = new List<string>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        var blankRows=0;var maxContent=0;
        var stagedRows = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        var pendingRows = new List<BsonDocument>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Bounded prefetch of immutable handles. Scope and manifest integrity are
            // checked exactly as on the ordinary read path; IDs never confer access.
            if (i % 64 == 0)
            {
                stagedRows.Clear();
                var references = table.Rows.Skip(i).Take(64).Where(r => r.Count == 2 && r[1].Value.ContentReference != null)
                    .Select(r => r[1].Value.ContentReference!).Distinct().ToArray();
                if (references.Length > 0)
                {
                    var manifests = await Collection(Manifests).Find(new BsonDocument { ["scope"] = scope,
                        ["_id"] = new BsonDocument("$in", new BsonArray(references.Select(r => r.Id))) }).ToListAsync(ct);
                    var manifestById = manifests.ToDictionary(m => m["_id"].AsString, StringComparer.Ordinal);
                    foreach (var reference in references)
                    {
                        if (!manifestById.TryGetValue(reference.Id, out var manifest)) throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
                        ValidateManifest(scope, reference, manifest);
                        if (reference.RowCount != 1) throw new AggregatePreviewException("AGG_CONTENT_TABLE_INVALID");
                    }
                    var rowIds = manifests.Select(m => m["rowIds"][0].AsString).Distinct().ToArray();
                    var rows = await Collection(Rows).Find(new BsonDocument { ["scope"] = scope,
                        ["_id"] = new BsonDocument("$in", new BsonArray(rowIds)) }).ToListAsync(ct);
                    var byId = rows.ToDictionary(r => r["_id"].AsString, StringComparer.Ordinal);
                    foreach (var reference in references)
                    {
                        if (!byId.TryGetValue(manifestById[reference.Id]["rowIds"][0].AsString, out var stagedRow))
                            throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE");
                        stagedRows.Add(reference.Id, stagedRow);
                    }
                }
            }
            var note = table.ContentRows[i]; var cells = table.Rows[i];
            if (!keys.Add(note.RowKey) || cells.Count != 2 || cells.Any(c => c.Value.Type != "TEXT")
                || cells[1].Value.State is not ("VALUE" or "BLANK")) throw new AggregatePreviewException("AGG_CONTENT_TABLE_INVALID");
            var text = cells[1].Value.Text ?? ""; var partIds = new List<string>();
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var characters=text.Length;var sample=Prefix(text,240);var blank=string.IsNullOrWhiteSpace(text);
            if(cells[1].Value.ContentReference is {} staged)
            {
                var sourceRow=stagedRows[staged.Id];
                partIds.AddRange(sourceRow["parts"].AsBsonArray.Select(p=>p.AsString));
                contentHash=sourceRow["contentHash"].AsString;characters=sourceRow["characters"].AsInt32;sample=sourceRow["sample"].AsString;blank=staged.BlankRows==1;
                if(sourceRow["state"].AsString!=cells[1].Value.State)throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
            }
            else
            {
                IEnumerable<BsonDocument> PartsToWrite()
                {
                    foreach (var part in Split(text))
                    {
                        if (partIds.Count >= 4096) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.content.row");
                        var id = Digest(scope, part); partIds.Add(id);
                        yield return new BsonDocument { ["_id"] = id, ["scope"] = scope, ["text"] = part };
                    }
                }
                await InsertImmutableMany(Parts, PartsToWrite(), gate, ct);
            }
            var noteJson = JsonSerializer.Serialize(note, AggregateCanonical.Json);
            if (Encoding.UTF8.GetByteCount(noteJson) > 65_536 || partIds.Count > 4096)
                throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.content.row");
            if(blank)blankRows++;maxContent=Math.Max(maxContent,characters);
            var hash = Digest(scope, JsonSerializer.Serialize(new { note, state = cells[1].Value.State, contentHash }, AggregateCanonical.Json));
            var row = new BsonDocument { ["_id"] = hash, ["scope"] = scope, ["sourceKey"] = note.SourceKey, ["rowKey"] = note.RowKey,
                ["note"] = noteJson, ["state"] = cells[1].Value.State, ["characters"] = characters,
                ["contentHash"] = contentHash,
                ["parts"] = new BsonArray(partIds), ["sample"] = sample };
            pendingRows.Add(row); ids.Add(hash);
            if (pendingRows.Count == 64 || i + 1 == table.Rows.Count)
            { await InsertImmutableMany(Rows, pendingRows, gate, ct); pendingRows.Clear(); }
        }
        // Publishing this small immutable manifest is the completion boundary. Until
        // it exists no partially captured table can be read or referenced by a report.
        var manifestHash = Digest(scope, JsonSerializer.Serialize(ids));
        var maxUnit = table.ContentRows.Select(n => n.UnitName.Length).DefaultIfEmpty().Max();
        await InsertImmutable(Manifests, new BsonDocument { ["_id"] = manifestHash, ["scope"] = scope,
            ["kind"] = Kind, ["rowIds"] = new BsonArray(ids), ["count"] = ids.Count,
            ["blankRows"] = blankRows, ["maxContentLength"] = maxContent, ["maxUnitLength"] = maxUnit }, gate, ct);
        return new(Kind, manifestHash, manifestHash, ids.Count, blankRows, maxContent, maxUnit);
    }

    internal async Task<AggregateContentPage> Page(string scope, AggregateContentReference reference, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 50) throw new AggregatePreviewException("AGG_CONTENT_PAGE_INVALID");
        var manifest = await Manifest(scope, reference, ct); var ids = manifest["rowIds"].AsBsonArray;
        if (offset > ids.Count) throw new AggregatePreviewException("AGG_CONTENT_PAGE_INVALID");
        var selected = ids.Skip(offset).Take(limit).Select(id => id.AsString).ToArray();
        var records = await Collection(Rows).Find(new BsonDocument { ["scope"] = scope, ["_id"] = new BsonDocument("$in", new BsonArray(selected)) }).ToListAsync(ct);
        var byId = records.ToDictionary(r => r["_id"].AsString, StringComparer.Ordinal);
        var result = new List<AggregateContentPageRow>();
        foreach (var id in selected)
        {
            if (!byId.TryGetValue(id, out var row)) throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE");
            var note = Note(row); var sample = row["sample"].AsString; var length = row["characters"].AsInt32;
            result.Add(new(note.RowKey, note.UnitName, note.ReportTitle, note.OccurrenceKey, note.CompletedDate, note.SubmittedAtUtc,
                row["state"].AsString, sample, length, length > sample.Length));
        }
        var next = offset + result.Count;
        return new(result, ids.Count, next < ids.Count ? next : null);
    }

    internal async Task<AggregateContentTextPart> TextPart(string scope, AggregateContentReference reference,
        string rowKey, int part, CancellationToken ct)
    {
        if (part < 0) throw new AggregatePreviewException("AGG_CONTENT_PAGE_INVALID");
        var row = await ReadRow(scope, reference, rowKey, ct);
        var parts = row["parts"].AsBsonArray;
        if (part == 0 && parts.Count == 0) return new("", null, row["characters"].AsInt32);
        if (part >= parts.Count) throw new AggregatePreviewException("AGG_CONTENT_PAGE_INVALID");
        var id = parts[part].AsString;
        var value = await Collection(Parts).Find(new BsonDocument { ["_id"] = id, ["scope"] = scope }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE");
        var text = value["text"].AsString;
        if (Digest(scope, text) != id) throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
        return new(text, part + 1 < parts.Count ? part + 1 : null, row["characters"].AsInt32);
    }

    internal async Task<AggregateContentRowNote> SourceNote(string scope, AggregateContentReference reference,
        string rowKey, CancellationToken ct) => Note(await ReadRow(scope, reference, rowKey, ct));

    private async Task<BsonDocument> ReadRow(string scope, AggregateContentReference reference, string rowKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rowKey)) throw new AggregatePreviewException("AGG_CONTENT_PAGE_INVALID");
        var manifest = await Manifest(scope, reference, ct);
        // Membership comes from the sealed manifest, not just the guessed row key.
        var row = await Collection(Rows).Find(new BsonDocument { ["scope"] = scope, ["rowKey"] = rowKey,
            ["_id"] = new BsonDocument("$in", manifest["rowIds"]) }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
        var note = Note(row); var state = row["state"].AsString; var contentHash = row["contentHash"].AsString;
        if (note.RowKey != rowKey || Digest(scope, JsonSerializer.Serialize(new { note, state, contentHash }, AggregateCanonical.Json)) != row["_id"].AsString)
            throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
        return row;
    }

    internal async Task<IReadOnlyList<string>> FindSourceRows(string scope, string sourceKey, CancellationToken ct)
    {
        var rows = await Collection(Rows).Find(new BsonDocument { ["scope"] = scope, ["sourceKey"] = sourceKey })
            .Project(new BsonDocument { ["_id"] = 0, ["rowKey"] = 1 }).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        return rows.Select(r => r["rowKey"].AsString).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal async Task<AggregateTable> ReadTable(string scope, AggregateContentReference reference, AggregateTableSchema schema, CancellationToken ct)
    {
        var manifest = await Manifest(scope, reference, ct);
        var orderedIds = manifest["rowIds"].AsBsonArray.Select(x => x.AsString).ToArray();
        var records = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        foreach (var ids in orderedIds.Chunk(128))
            foreach (var row in await Collection(Rows).Find(new BsonDocument { ["scope"] = scope, ["_id"] = new BsonDocument("$in", new BsonArray(ids)) }).ToListAsync(ct))
                records.Add(row["_id"].AsString, row);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal); long characters = 0;
        foreach (var ids in records.Values.SelectMany(r => r["parts"].AsBsonArray.Select(x => x.AsString)).Distinct().Chunk(128))
            foreach (var part in await Collection(Parts).Find(new BsonDocument { ["scope"] = scope, ["_id"] = new BsonDocument("$in", new BsonArray(ids)) }).ToListAsync(ct))
            {
                var text = part["text"].AsString; characters += text.Length;
                if (characters > 8_000_000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.content.materialize");
                if (Digest(scope, text) != part["_id"].AsString) throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
                parts.Add(part["_id"].AsString, text);
            }
        var notes = new List<AggregateContentRowNote>(); var rows = new List<IReadOnlyList<AggregateCell>>();
        long materializedCharacters = 0;
        foreach (var id in orderedIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!records.TryGetValue(id, out var row)) throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE");
            materializedCharacters += row["characters"].AsInt32;
            if (materializedCharacters > 8_000_000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.content.materialize");
            var note = Note(row); notes.Add(note); var body = new StringBuilder();
            foreach (var part in row["parts"].AsBsonArray)
            { if (!parts.TryGetValue(part.AsString, out var text)) throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE"); body.Append(text); }
            var content = body.ToString();
            if (content.Length != row["characters"].AsInt32 || Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))) != row["contentHash"].AsString
                || Digest(scope, JsonSerializer.Serialize(new { note, state = row["state"].AsString, contentHash = row["contentHash"].AsString }, AggregateCanonical.Json)) != id)
                throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
            var trace = new[] { new AggregateTrace(note.ReportId, note.UnitId, note.OccurrenceKey, note.MemberId) };
            rows.Add([new(new("TEXT", "VALUE", Text: note.UnitName), trace), new(new("TEXT", row["state"].AsString, Text: content), trace)]);
        }
        return new(schema, rows, notes.Select(n => n.UnitId).ToArray()) { ContentRows = notes };
    }

    internal async Task WriteSnapshot(string scope, AggregateContentReference reference, Stream destination, CancellationToken ct)
    {
        var manifest=await Manifest(scope,reference,ct);
        long bytes=0;
        async Task Line(object value)
        {
            var encoded=JsonSerializer.SerializeToUtf8Bytes(value,AggregateCanonical.Json);
            bytes+=encoded.Length+1;
            if(bytes>536_870_912)throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED","$.content.snapshot.bytes");
            await destination.WriteAsync(encoded,ct);await destination.WriteAsync(new byte[]{10},ct);
        }
        await Line(new{kind="REPORT_CONTENT_SNAPSHOT_V1",reference});
        foreach(var id in manifest["rowIds"].AsBsonArray)
        {
            var row=await Collection(Rows).Find(new BsonDocument{["_id"]=id,["scope"]=scope}).FirstOrDefaultAsync(ct)
                ??throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE");
            var note=Note(row);var contentHash=row["contentHash"].AsString;var state=row["state"].AsString;
            if(Digest(scope,JsonSerializer.Serialize(new{note,state,contentHash},AggregateCanonical.Json))!=id.AsString)
                throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
            await Line(new{kind="row",note=Note(row),state=row["state"].AsString,characters=row["characters"].AsInt32,contentHash=row["contentHash"].AsString});
            var sequence=0;
            using var bodyHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long characters=0;
            foreach(var partId in row["parts"].AsBsonArray)
            {
                var part=await Collection(Parts).Find(new BsonDocument{["_id"]=partId,["scope"]=scope}).FirstOrDefaultAsync(ct)
                    ??throw new AggregatePreviewException("AGG_CONTENT_INCOMPLETE");
                var text=part["text"].AsString;
                if(Digest(scope,text)!=partId.AsString)throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
                characters+=text.Length;bodyHash.AppendData(Encoding.UTF8.GetBytes(text));
                await Line(new{kind="part",rowKey=row["rowKey"].AsString,sequence=sequence++,text});
            }
            if(characters!=row["characters"].AsInt32||Convert.ToHexString(bodyHash.GetHashAndReset())!=contentHash)
                throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
        }
    }
    internal async Task<BsonDocument> Manifest(string scope, AggregateContentReference reference, CancellationToken ct)
    {
        if (reference.Kind != Kind || reference.Id != reference.Hash) throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
        var row = await Collection(Manifests).Find(new BsonDocument { ["_id"] = reference.Id, ["scope"] = scope }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
        ValidateManifest(scope, reference, row);
        return row;
    }
    private static void ValidateManifest(string scope, AggregateContentReference reference, BsonDocument row)
    {
        if (reference.Kind != Kind || reference.Id != reference.Hash || row["scope"] != scope || row["_id"] != reference.Id)
            throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
        var ids = row["rowIds"].AsBsonArray.Select(x => x.AsString).ToArray();
        if (ids.Length != reference.RowCount || row["count"].AsInt32 != ids.Length || Digest(scope, JsonSerializer.Serialize(ids)) != reference.Hash
            || row["blankRows"].AsInt32 != reference.BlankRows || row["maxContentLength"].AsInt32 != reference.MaxContentLength
            || row["maxUnitLength"].AsInt32 != reference.MaxUnitLength)
            throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
    }
    private async Task InsertImmutableMany(string collection, IEnumerable<BsonDocument> rows, AggregateContentScopeGate gate, CancellationToken ct)
    {
        // Bound both count and bytes. Read existing immutable records once per batch;
        // on a concurrent insert, verify the winner rather than overwrite or swallow errors.
        var batch = new List<BsonDocument>(); var bytes = 0;
        async Task Flush()
        {
            if (batch.Count == 0) return;
            await gate.Ensure(ct);
            var wanted = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
            foreach (var row in batch)
            {
                var id = row["_id"].AsString;
                if (wanted.TryGetValue(id, out var previous) && !previous.Equals(row)) throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
                wanted[id] = row;
            }
            var existing = await Collection(collection).Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(wanted.Keys)))).ToListAsync(ct);
            foreach (var row in existing)
            {
                if (!wanted[row["_id"].AsString].Equals(row)) throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
                wanted.Remove(row["_id"].AsString);
            }
            if (wanted.Count > 0)
            {
                try { await Collection(collection).InsertManyAsync(wanted.Values, new InsertManyOptions { IsOrdered = false }, ct); }
                catch (MongoBulkWriteException<BsonDocument> ex) when (ex.WriteConcernError == null && ex.WriteErrors.Count > 0
                    && ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
                {
                    var winners = await Collection(collection).Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(wanted.Keys)))).ToListAsync(ct);
                    if (winners.Count != wanted.Count || winners.Any(r => !wanted[r["_id"].AsString].Equals(r)))
                        throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
                }
            }
            batch.Clear(); bytes = 0;
        }
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested(); var size = row.ToBson().Length;
            if (size > 1_048_576) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED", "$.content.batch");
            if (batch.Count >= 32 || bytes + size > 1_048_576) await Flush();
            batch.Add(row); bytes += size;
        }
        await Flush();
    }
    private async Task InsertImmutable(string collection, BsonDocument row, AggregateContentScopeGate gate, CancellationToken ct)
    {
        await gate.Ensure(ct);
        try { await Collection(collection).InsertOneAsync(row, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await Collection(collection).Find(new BsonDocument("_id", row["_id"])).SingleAsync(ct);
            if (!existing.Equals(row)) throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
        }
    }
    private static AggregateContentRowNote Note(BsonDocument row) => JsonSerializer.Deserialize<AggregateContentRowNote>(row["note"].AsString, AggregateCanonical.Json)
        ?? throw new AggregatePreviewException("AGG_CONTENT_INTEGRITY");
    private static string Digest(string scope, string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + text)));
    private static string Prefix(string text, int size) => text[..(text.Length > size && char.IsHighSurrogate(text[size - 1]) ? size - 1 : Math.Min(text.Length, size))];
    private static IEnumerable<string> Split(string text)
    {
        for (var offset = 0; offset < text.Length;)
        {
            var size = Math.Min(PartCharacters, text.Length - offset);
            if (offset + size < text.Length && char.IsHighSurrogate(text[offset + size - 1])) size--;
            yield return text.Substring(offset, size); offset += size;
        }
    }
}
