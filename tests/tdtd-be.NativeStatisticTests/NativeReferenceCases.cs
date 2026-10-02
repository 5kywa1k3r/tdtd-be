using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class NativeReferenceCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database)
    {
        var db = new MongoClient(connection).GetDatabase(database + "_references");
        foreach (var kind in new[] { "BASIC_SNAPSHOT", "ADVANCED_SNAPSHOT", "BASIC_REFRESH", "ADVANCED_REFRESH" })
        {
            await test(kind + " registry commits and lost-response replay preserves exact source", async () => {
                var f = new Fixture(db, kind); await f.Tx(f.Write);
                var source = await f.Source(); var reference = await f.Reference();
                Check(reference["sourceId"] == f.Id && reference["snapshotId"] == f.SnapshotId, "identity");
                Check(reference["dynamicFormTemplateId"] == f.Form && reference["actorId"] == f.Actor, "binding");
                Check(reference["sourceContentSha256"] == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source["json"].AsString))).ToLowerInvariant(), "content hash");
                await f.Tx(f.Write); Check((await f.Source()).Equals(source) && (await f.Reference()).Equals(reference), "replay changed raw");
            });
            await test(kind + " registry caller abort rolls both inserts back", async () => {
                var f = new Fixture(db, kind);
                try { await f.Tx(async session => { await f.Write(session); throw new InvalidOperationException("injected after pair"); }); }
                catch (InvalidOperationException) { }
                Check(await f.CountSource() == 0 && await f.CountReference() == 0, "partial commit");
            });
            await test(kind + " registry conflict after source insert rolls source back and keeps evidence", async () => {
                var f = new Fixture(db, kind); var damaged = new BsonDocument { { "_id", f.ReferenceId }, { "raw", "retain" } };
                await f.Registry.InsertOneAsync(damaged);
                await Reject(() => f.Tx(f.Write));
                Check(await f.CountSource() == 0 && (await f.Reference()).Equals(damaged), "conflict repaired or source escaped");
            });
            await test(kind + " registry concurrent identical writes converge", async () => {
                var f = new Fixture(db, kind);
                async Task Write() { try { await f.Tx(f.Write); } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { await f.Tx(f.Write); } }
                await Task.WhenAll(Write(), Write());
                Check(await f.CountSource() == 1 && await f.CountReference() == 1, "duplicate pair");
            });
            await test(kind + " registry missing old entry is not backfilled by read", async () => {
                var f = new Fixture(db, kind); await f.Tx(f.Write); var before = await f.Source();
                await f.Registry.DeleteOneAsync(new BsonDocument("_id", f.ReferenceId)); // isolated legacy fixture
                await f.Read(); Check(await f.CountReference() == 0, "getter wrote registry");
                await f.Tx(f.Write); Check(await f.CountReference() == 1 && (await f.Source()).Equals(before), "explicit replay changed source");
            });
            await test(kind + " registry rejects mutation of existing source", async () => {
                var f = new Fixture(db, kind); await f.Tx(f.Write); var reference = await f.Reference();
                await f.Rows.UpdateOneAsync(new BsonDocument("_id", f.Id), new BsonDocument("$set", new BsonDocument("hash", "broken")));
                var before = await f.Source(); await Reject(() => f.Tx(f.Write));
                Check((await f.Source()).Equals(before) && (await f.Reference()).Equals(reference), "damaged evidence changed");
            });
            if (kind.EndsWith("REFRESH", StringComparison.Ordinal)) await test(kind + " mutable attempt does not alter immutable reference", async () => {
                var f = new Fixture(db, kind); await f.Tx(f.Write); var before = await f.Reference();
                if (kind == "BASIC_REFRESH") { var s = new BasicNativeRefreshStore(db); var e = await s.ReadAsync(f.Id, default); await s.FailAsync(await s.ClaimAsync(e, default), "test", default); }
                else { var s = new AdvancedNativeRefreshStore(db); var e = await s.ReadAsync(f.Id, default); await s.FailAsync(await s.ClaimAsync(e, default), "test", default); }
                var source = await f.Source(); await f.Tx(f.Write);
                Check((await f.Reference()).Equals(before) && (await f.Source()).Equals(source), "replay reset attempt");
            });
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (AppException) { return; } throw new Exception("Expected rejection"); }
    private sealed class Fixture
    {
        private readonly IMongoDatabase db; private readonly string kind;
        internal readonly string Form = ObjectId.GenerateNewId().ToString(), Actor = ObjectId.GenerateNewId().ToString();
        private readonly string scope = ObjectId.GenerateNewId().ToString(), section = ObjectId.GenerateNewId().ToString();
        internal readonly string SnapshotId = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();
        internal string Id { get; }
        internal string ReferenceId => kind + ":" + Id;
        internal IMongoCollection<BsonDocument> Registry => db.GetCollection<BsonDocument>(NativeStorageReferenceStore.CollectionName);
        internal IMongoCollection<BsonDocument> Rows { get; }
        private readonly BasicNativeSnapshot basic; private readonly AdvancedNativeSnapshot advanced;
        internal Fixture(IMongoDatabase db, string kind)
        {
            this.db = db; this.kind = kind; Id = kind.EndsWith("SNAPSHOT", StringComparison.Ordinal) ? SnapshotId : ObjectId.GenerateNewId().ToString();
            Rows = db.GetCollection<BsonDocument>(kind switch { "BASIC_SNAPSHOT" => BasicNativeSnapshotStore.CollectionName,
                "ADVANCED_SNAPSHOT" => AdvancedNativeSnapshotStore.CollectionName, "BASIC_REFRESH" => BasicNativeRefreshStore.CollectionName,
                _ => AdvancedNativeRefreshStore.CollectionName });
            var a = Fixtures.Artifact(901); var hash = new string('a', 64);
            var native = StatConfigCanonicalJson.DeserializeStrict<NativeStatisticResultDocument>(JsonSerializer.SerializeToElement(a.Result, StatConfigCanonicalJson.StrictJsonOptions));
            var config = new StatConfigIdentity("fixture", scope, scope, scope, 1, 1, "LOCKED", hash, []);
            basic = new(1, SnapshotId, scope, Actor, [scope], a.DefinitionJson, a.ConfigurationJson, "{}", "{}", hash, config,
                new WorkAssignmentBasicSummaryResponse { Meta = new() { DynamicFormTemplateId = Form, ScopeAssignmentId = scope } }, native);
            advanced = new(1, SnapshotId, scope, Actor, [scope], Form, section, "DAY", "2026-09-18", DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(1),
                a.DefinitionJson, a.ConfigurationJson, "{}", "{}", hash, config, JsonSerializer.SerializeToElement(new { }), native, null);
        }
        internal async Task Write(IClientSessionHandle session)
        {
            if (kind == "BASIC_SNAPSHOT") await new BasicNativeSnapshotStore(db).StoreAsync(session, basic, default);
            else if (kind == "ADVANCED_SNAPSHOT") await new AdvancedNativeSnapshotStore(db).StoreAsync(session, advanced, default);
            else if (kind == "BASIC_REFRESH") await new BasicNativeRefreshStore(db).CreateAsync(session, new(1, Id, Actor, new string('b', 64), new(scope, Form, SnapshotId: SnapshotId)), default);
            else await new AdvancedNativeRefreshStore(db).CreateAsync(session, new(1, Id, Actor, new string('b', 64), new(scope, Form, section, "DAY", "2026-09-18", SnapshotId)), default);
        }
        internal async Task Read()
        {
            if (kind == "BASIC_SNAPSHOT") _ = await new BasicNativeSnapshotStore(db).ReadAsync(Id, default);
            else if (kind == "ADVANCED_SNAPSHOT") _ = await new AdvancedNativeSnapshotStore(db).ReadAsync(Id, default);
            else if (kind == "BASIC_REFRESH") _ = await new BasicNativeRefreshStore(db).ReadAsync(Id, default);
            else _ = await new AdvancedNativeRefreshStore(db).ReadAsync(Id, default);
        }
        internal async Task Tx(Func<IClientSessionHandle, Task> action)
        {
            using var session = await db.Client.StartSessionAsync();
            await session.WithTransactionAsync(async (s, _) => { await action(s); return true; }, new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        }
        internal Task<BsonDocument> Source() => Rows.Find(new BsonDocument("_id", Id)).SingleAsync();
        internal Task<BsonDocument> Reference() => Registry.Find(new BsonDocument("_id", ReferenceId)).SingleAsync();
        internal Task<long> CountSource() => Rows.CountDocumentsAsync(new BsonDocument("_id", Id));
        internal Task<long> CountReference() => Registry.CountDocumentsAsync(new BsonDocument("_id", ReferenceId));
    }
}
