using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

// Extends the same 200-value workload: replace four numeric fields, never pad with unused fields.
internal static class WideContentChecks
{
    private static readonly string Body = new string('x', 16383) + "📝\n" + string.Concat(Enumerable.Repeat("Nội dung rất dài, tiếng Việt.\n", 2000));
    internal static string Text(int ordinal, int field) => ordinal == 166 && field == 4 ? ""
        : $"Nguồn {ordinal:D3}; trường {field}.\n" + Body + $"\nHết nguồn {ordinal}; trường {field}.";
    internal static DynamicFormNativeTableDto Table(int i) => new() {
        Id = "content" + i, SectionId = "main", Name = "Nội dung " + i, Layout = "vertical", Order = 40 + i,
        Fields = [new() { Id = "unitCol" + i, Name = "Đơn vị báo cáo", Order = 0 }, new() { Id = "bodyCol" + i, Name = "Nội dung", Order = 1 }],
        Rows = [], StatisticTargets = [], TypeConfig = new() { Version = 1, Sequence = 2, Rules = [
            new() { Order = 1, Target = new() { Scope = "column", FieldId = "unitCol" + i }, Spec = new() { Type = "plainText", Required = false } },
            new() { Order = 2, Target = new() { Scope = "column", FieldId = "bodyCol" + i }, Spec = new() { Type = "plainText", Required = false } }] }
    };
    internal static void AddMapping(AggregateRecipeDto recipe, int i)
    {
        var member = "text" + i; var target = "content" + i; var calculation = "contentCalc" + i;
        recipe.Nodes.Single(n => n.Kind == "SOURCE").Outputs.Add(new(member, "TEXT", "SET", member, "w"));
        recipe.Nodes.Single(n => n.Kind == "TARGET").Inputs.Add(new(target, "TABLE", "SINGLE", target));
        recipe.Nodes.Add(new() { Id = calculation, Kind = "CALCULATION", Inputs = [new("in", "TEXT", "SET")], Outputs = [new("out", "TABLE", "SINGLE")],
            Expressions = [new("out", new() { Kind = "CALL", Name = "REPORT_TEXT_TABLE", Arguments = [new() { Kind = "INPUT", Ref = "in" }], Options = new() { Order = "UNIT_THEN_PERIOD" } })] });
        recipe.Edges.Add(new("textIn" + i, new("s", member), new(calculation, "in")));
        recipe.Edges.Add(new("textOut" + i, new(calculation, "out"), new("t", target)));
    }
    internal static async Task ReadBack(MongoDbContext db, RuntimeFixture fixture, string tablesJson, int payloadRevision,
        Func<string, object, Task<JsonElement>> call, Action<bool, string> check, CancellationToken ct)
    {
        var native = JsonSerializer.Deserialize<JsonElement>(tablesJson).GetProperty("nativeTables").GetProperty("tables");
        for (var field = 1; field <= 4; field++)
        {
            var tableId = "content" + field;
            var reference = native.EnumerateArray().Single(t => t.GetProperty("tableId").GetString() == tableId).GetProperty("contentRef");
            var request = new AggregateContentReadRequest(fixture.Report.Id, tableId, payloadRevision, null, reference);
            var rows = new List<JsonElement>(); int? offset = 0;
            while (offset != null)
            {
                var page = await call("content/page", request with { Offset = offset.Value });
                rows.AddRange(page.GetProperty("rows").EnumerateArray());
                offset = page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextOffset").GetInt32();
            }
            check(rows.Count == 166 && rows.Select(r => r.GetProperty("rowKey").GetString()).Distinct().Count() == 166,
                "mixed native content all 166 distinct units " + tableId);
            var blanks = rows.Where(r => r.GetProperty("state").GetString() == "BLANK" || r.GetProperty("characterCount").GetInt32() == 0).ToArray();
            check(blanks.Length == (field == 4 ? 1 : 0), "mixed content blank row preserved " + tableId);
            var row = rows.First(r => r.GetProperty("characterCount").GetInt32() > 0);
            int? part = 0; var full = new StringBuilder();
            while (part != null)
            {
                var content = await call("content/part", request with { RowKey = row.GetProperty("rowKey").GetString(), Part = part.Value });
                full.Append(content.GetProperty("text").GetString());
                part = content.GetProperty("nextPart").ValueKind == JsonValueKind.Null ? null : content.GetProperty("nextPart").GetInt32();
            }
            var text = full.ToString(); var ordinal = int.Parse(text.Substring("Nguồn ".Length, 3));
            check(text == Text(ordinal, field), "mixed native text parts preserve exact Unicode and field identity " + tableId);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var rows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots)
                .Find(new BsonDocument { ["workId"] = fixture.WorkId, ["target"] = fixture.Report.Id }).ToListAsync(ct);
            var intents = rows.Where(r => AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value.PayloadRevision == payloadRevision).ToArray();
            if (intents.Length == 4 && intents.All(r => r.GetValue("state", "PENDING") == "READY")) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("mixed content archives did not complete");
            await Task.Delay(200, ct);
        }
        check(true, "mixed all four snapshots delivered through real Hangfire to MinIO");
    }
}
