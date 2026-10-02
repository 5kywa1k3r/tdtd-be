using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.NativeStatisticTests;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.CanvasRoundtripTests;

// Storage-contract fixtures, not evidence of a real aggregation/worker lifecycle.
internal static class CanvasSnapshotFixture
{
    internal static void Map(WebApplication app)
    {
        static string Collection(string kind, bool refresh) => (kind, refresh) switch {
            ("BASIC", false) => BasicNativeSnapshotStore.CollectionName, ("ADVANCED", false) => AdvancedNativeSnapshotStore.CollectionName,
            ("BASIC", true) => BasicNativeRefreshStore.CollectionName, ("ADVANCED", true) => AdvancedNativeRefreshStore.CollectionName,
            _ => throw new ArgumentException("Fixture kind") };
        app.MapPost("/__canvas/snapshot/{ownerId}/{kind}", async (string ownerId, string kind, MongoDbContext db) =>
        {
            var snapshotId = StatRunCanonicalJson.HashText(Guid.NewGuid().ToString()); var refreshId = ObjectId.GenerateNewId().ToString();
            var scope = ObjectId.GenerateNewId().ToString(); var actor = ObjectId.GenerateNewId().ToString(); var section = ObjectId.GenerateNewId().ToString();
            var hash = new string('a', 64); var source = Fixtures.Artifact(701);
            var native = StatConfigCanonicalJson.DeserializeStrict<NativeStatisticResultDocument>(
                JsonSerializer.SerializeToElement(source.Result, StatConfigCanonicalJson.StrictJsonOptions));
            var identity = new StatConfigIdentity(kind, scope, scope, scope, 1, 1, "LOCKED", hash, []);
            using var session = await db.Db.Client.StartSessionAsync(); session.StartTransaction();
            if (kind == "BASIC")
                await new BasicNativeSnapshotStore(db.Db).StoreAsync(session, new(1, snapshotId, scope, actor, [scope],
                    source.DefinitionJson, source.ConfigurationJson, "{}", "{}", hash, identity,
                    new() { Meta = new() { DynamicFormTemplateId = ownerId, ScopeAssignmentId = scope } }, native), default);
            else if (kind == "ADVANCED")
                await new AdvancedNativeSnapshotStore(db.Db).StoreAsync(session, new(1, snapshotId, scope, actor, [scope], ownerId, section,
                    "DAY", "2026-09-18", DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(1), source.DefinitionJson,
                    source.ConfigurationJson, "{}", "{}", hash, identity, JsonSerializer.SerializeToElement(new { }), native, null), default);
            else throw new ArgumentException("Fixture kind");
            if (kind == "BASIC") await new BasicNativeRefreshStore(db.Db).CreateAsync(session, new(1, refreshId, actor, hash,
                new(scope, ownerId, SnapshotId: snapshotId)), default);
            else await new AdvancedNativeRefreshStore(db.Db).CreateAsync(session, new(1, refreshId, actor, hash,
                new(scope, ownerId, section, "DAY", "2026-09-18", snapshotId)), default);
            await session.CommitTransactionAsync();
            return new { snapshotId, refreshId };
        }).RequireAuthorization();
        app.MapGet("/__canvas/snapshot/{kind}/{snapshotId}/{refreshId}", async (string kind, string snapshotId, string refreshId, MongoDbContext db) =>
        {
            var snapshot = await db.Db.GetCollection<BsonDocument>(Collection(kind, false)).Find(new BsonDocument("_id", snapshotId)).SingleOrDefaultAsync();
            var refresh = await db.Db.GetCollection<BsonDocument>(Collection(kind, true)).Find(new BsonDocument("_id", refreshId)).SingleOrDefaultAsync();
            return new { snapshot = snapshot is null ? null : Convert.ToBase64String(snapshot.ToBson()),
                refresh = refresh is null ? null : Convert.ToBase64String(refresh.ToBson()) };
        }).RequireAuthorization();
        app.MapPost("/__canvas/snapshot-corrupt/{kind}/{snapshotId}/{refreshId}", async (string kind, string snapshotId, string refreshId, JsonElement body, MongoDbContext db) =>
        {
            var mode = body.GetProperty("mode").GetString()!;
            var isRefresh = mode.StartsWith("refresh-", StringComparison.Ordinal);
            var rows = db.Db.GetCollection<BsonDocument>(Collection(kind, isRefresh)); var filter = new BsonDocument("_id", isRefresh ? refreshId : snapshotId);
            if (mode is "missing" or "refresh-missing") { await rows.DeleteOneAsync(filter); return; }
            var row = await rows.Find(filter).SingleAsync();
            if (mode == "hash") row["hash"] = new string('0', 64);
            else if (mode == "bytes") row["bytes"] = row["bytes"].AsInt32 + 1;
            else if (mode == "refresh-completed-mismatch") { row["state"] = "COMPLETED"; row["snapshotHash"] = new string('0', 64); }
            else if (mode == "refresh-completed") {
                var snapshot = await db.Db.GetCollection<BsonDocument>(Collection(kind, false)).Find(new BsonDocument("_id", snapshotId)).SingleAsync();
                row["state"] = "COMPLETED"; row["snapshotHash"] = snapshot["hash"]; }
            else
            {
                var json = row["json"].AsString;
                if (mode is "duplicate" or "refresh-duplicate") json = "{\"version\":1," + json[1..];
                else if (mode == "unknown") json = "{\"unexpected\":true," + json[1..];
                else if (mode == "refresh-snapshot") json = json.Replace(snapshotId, new string('b', 64), StringComparison.Ordinal);
                else throw new ArgumentException("Fixture mode");
                row["json"] = json; row["hash"] = StatRunCanonicalJson.HashText(json);
                if (!isRefresh) row["bytes"] = Encoding.UTF8.GetByteCount(json);
            }
            await rows.ReplaceOneAsync(filter, row);
        }).RequireAuthorization();
    }
}
