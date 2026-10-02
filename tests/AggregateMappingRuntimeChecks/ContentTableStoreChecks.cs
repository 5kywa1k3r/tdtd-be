using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class ContentTableStoreChecks
{
    internal static async Task Run(MongoDbContext db, string run, CancellationToken ct)
    {
        var store = new AggregateContentTableStore(db); await store.CreateIndexes(ct);
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS content-store " + label); }
        async Task Error(Func<Task> action, string code)
        { try { await action(); throw new Exception("Expected " + code); } catch (AggregatePreviewException ex) { Check(ex.Code == code, code); } }
        var text = new string('x', 16_383) + "📝\n" + string.Concat(Enumerable.Repeat("Tiếng Việt, giữ toàn văn.\n", 2_000));
        AggregateObservation Item(int i, string? value) => new(value == null ? AggregateValue.Blank("TEXT") : new("TEXT", "VALUE", Text: value),
            [new("report" + i, "unit" + i, "ONCE", "body")]) { SourceNote = new("", "slot" + i + ":body", "report" + i,
                "unit" + i, "Đơn vị " + i, "Báo cáo " + i, "ONCE", "body", null, null) };
        var source = new AggregateChannel("TEXT", "SET", Enumerable.Range(0, 166).Select(i => Item(i, i == 0 ? null : text + i)).ToArray());
        AggregateTable Table(AggregateChannel c) => AggregateEvaluator.Scalar(AggregateContentTable.Build(c,
            new() { Order = "UNIT_THEN_PERIOD" }, new(default))).Value.Table!;
        var table = Table(source); var watch = Stopwatch.StartNew();
        var reference = await store.Capture(run, table, ct);
        Console.WriteLine($"MEASURE content-store rows=166 charactersPerNonblankRow={text.Length}+suffix captureMs={watch.ElapsedMilliseconds}");
        Check(reference.RowCount == 166, "sealed 166-row manifest");
        var first = await store.Page(run, reference, 0, 10, ct);
        Check(first.Rows.Count == 10 && first.NextOffset == 10 && first.Total == 166, "page returns only ten samples with continuation");
        Check(first.Rows[0].State == "BLANK" && first.Rows[0].Sample == "" && !first.Rows[0].HasMore, "blank approved report retained");
        Check(first.Rows.Skip(1).All(r => r.Sample.Length <= 240 && r.HasMore), "long cells are referenced rather than sent whole");
        var longRow = first.Rows[1]; var restored = new System.Text.StringBuilder(); int? part = 0;
        while (part != null)
        {
            var read = await store.TextPart(run, reference, longRow.RowKey, part.Value, ct);
            Check(read.Text.Length <= 16_384 && (read.Text.Length == 0 || !char.IsHighSurrogate(read.Text[^1])), "bounded Unicode-safe text part");
            restored.Append(read.Text); part = read.NextPart;
        }
        var position = table.ContentRows!.ToList().FindIndex(r => r.RowKey == longRow.RowKey);
        Check(restored.ToString() == table.Rows[position][1].Value.Text, "all parts restore exact original content");
        await Error(async () => { await store.Page(run + "-other", reference, 0, 10, ct); }, "AGG_CONTENT_UNAVAILABLE");
        await Error(async () => { await store.TextPart(run, reference, "not-in-manifest", 0, ct); }, "AGG_CONTENT_UNAVAILABLE");
        await Error(async () => { await store.Page(run, reference, 0, 51, ct); }, "AGG_CONTENT_PAGE_INVALID");
        await Error(async () => { await store.Page(run, reference with { RowCount = 1 }, 0, 10, ct); }, "AGG_CONTENT_INTEGRITY");
        var rowCollection = db.Db.GetCollection<BsonDocument>(AggregateContentTableStore.Rows);
        var before = await rowCollection.CountDocumentsAsync(new BsonDocument("scope", run), cancellationToken: ct);
        Check(await store.Capture(run, table, ct) == reference && before == await rowCollection.CountDocumentsAsync(new BsonDocument("scope", run), cancellationToken: ct), "repeated capture is idempotent");
        var changed = source with { Items = source.Items.Select((item, i) => i == 1 ? item with { Value = new("TEXT", "VALUE", Text: "Nội dung thay đổi") } : item).ToArray() };
        var next = await store.Capture(run, Table(changed), ct);
        Check(next.Id != reference.Id && before + 1 == await rowCollection.CountDocumentsAsync(new BsonDocument("scope", run), cancellationToken: ct), "one changed source writes one new row version");
        Check((await store.Page(run, reference, 0, 10, ct)).Rows[1].CharacterCount == longRow.CharacterCount, "old sealed snapshot remains readable");
        Check((await store.FindSourceRows(run, "slot1:body", ct)).Count == 1, "source lookup returns stable identity across row versions");
        var explain = await db.Db.RunCommandAsync<BsonDocument>(new BsonDocument { ["explain"] = new BsonDocument {
            ["find"] = AggregateContentTableStore.Rows, ["filter"] = new BsonDocument { ["scope"] = run, ["sourceKey"] = "slot1:body" },
            ["projection"] = new BsonDocument { ["_id"] = 0, ["rowKey"] = 1 } }, ["verbosity"] = "executionStats" }, cancellationToken: ct);
        var stats = explain["executionStats"].AsBsonDocument;
        Check(stats["totalKeysExamined"].ToInt64() <= 2 && stats["totalDocsExamined"].ToInt64() <= 2 && stats["nReturned"].ToInt64() == 2,
            "real index lookup examines only two versions of the changed source");
        Console.WriteLine($"MEASURE content-index keysExamined={stats["totalKeysExamined"]} docsExamined={stats["totalDocsExamined"]} returned={stats["nReturned"]}");
        var raceScope = run + "-race";
        var raceTable = Table(source with { Items = source.Items.Take(12).ToArray() });
        var raced = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => new AggregateContentTableStore(db).Capture(raceScope, raceTable, ct)));
        Check(raced.All(r => r == raced[0]) && await rowCollection.CountDocumentsAsync(new BsonDocument("scope", raceScope), cancellationToken: ct) == 12,
            "concurrent batched capture converges to one immutable set without duplicate rows");
        var parts = db.Db.GetCollection<BsonDocument>(AggregateContentTableStore.Parts);
        var originalPart = await parts.Find(new BsonDocument("scope", raceScope)).FirstAsync(ct);
        await parts.UpdateOneAsync(new BsonDocument("_id", originalPart["_id"]), new BsonDocument("$set", new BsonDocument("text", "tampered fixture")), cancellationToken: ct);
        try { await Error(async () => { await new AggregateContentTableStore(db).Capture(raceScope, raceTable, ct); }, "AGG_CONTENT_INTEGRITY"); }
        finally { await parts.ReplaceOneAsync(new BsonDocument("_id", originalPart["_id"]), originalPart, cancellationToken: ct); }
        Check((await store.Page(raceScope, raced[0], 0, 10, ct)).Total == 12, "failed duplicate integrity check does not replace the sealed manifest");
        Console.WriteLine("PASS content-store slice complete; internal storage only, no HTTP ACL/native Apply/browser/MinIO claim; fixtures retained.");
    }
}
