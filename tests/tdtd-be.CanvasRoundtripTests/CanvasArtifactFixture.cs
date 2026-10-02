using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.Statistics;
using tdtd_be.NativeStatisticTests;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.CanvasRoundtripTests;

// Synthetic retained publication only, using the existing calculator/storage fixture.
// No application scheduler, catalog activation or live database is involved.
internal static class CanvasArtifactFixture
{
    internal static void Map(WebApplication app)
    {
        app.MapPost("/__canvas/artifact/{ownerId}", async (string ownerId, JsonElement body, MongoDbContext db) =>
        {
            var artifact = Fixtures.Artifact(body.GetProperty("identity").GetInt32());
            artifact = artifact with { Generation = artifact.Generation with { DynamicFormTemplateId = ownerId } };
            var receipt = await new NativeStatisticArtifactStore(db.Db).StageAsync(artifact, new(8 * 1024 * 1024, 1024, 384));
            var pin = artifact.Generation;
            var job = new WorkReportStatisticRebuildJob { Id = pin.RunId, WorkId = artifact.WorkId, PeriodInstanceKey = artifact.PeriodInstanceKey,
                SourceLifecycleEventKey = pin.LifecycleEventKey, TotalReportCount = artifact.Sources.Count,
                NativeStatisticPublication = NativeStatisticPublicationContract.Create(receipt, artifact) };
            // Copy matching immutable generation fields; the separately implemented reader verifies every binding.
            foreach (var property in pin.GetType().GetProperties())
            {
                var destination = typeof(WorkReportStatisticRebuildJob).GetProperty(property.Name);
                if (destination?.CanWrite == true && (destination.PropertyType == property.PropertyType
                    || Nullable.GetUnderlyingType(destination.PropertyType) == property.PropertyType)) destination.SetValue(job, property.GetValue(pin));
            }
            await db.WorkReportStatisticRebuildJobs.InsertOneAsync(job);
            return new { runId = job.Id, headerId = StatRunCanonicalJson.HashText("NATIVE_STAGE_V2\n" + job.Id + "\n" + job.GenerationId), receipt.ArtifactHash };
        }).RequireAuthorization();
        app.MapGet("/__canvas/artifact/{runId}/{headerId}", async (string runId, string headerId, MongoDbContext db) =>
        {
            var job = await db.Db.GetCollection<BsonDocument>(db.WorkReportStatisticRebuildJobs.CollectionNamespace.CollectionName)
                .Find(new BsonDocument("_id", ObjectId.Parse(runId))).SingleAsync();
            var rows = await db.Db.GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName)
                .Find(new BsonDocument("_id", new BsonDocument { { "$gte", headerId }, { "$lt", headerId + "0" } })).Sort(new BsonDocument("_id", 1)).ToListAsync();
            return new { job = Convert.ToBase64String(job.ToBson()), rows = rows.Select(row => Convert.ToBase64String(row.ToBson())) };
        }).RequireAuthorization();
        app.MapPost("/__canvas/artifact-corrupt/{runId}/{headerId}", async (string runId, string headerId, JsonElement body, MongoDbContext db) =>
        {
            var mode = body.GetProperty("mode").GetString();
            var artifacts = db.Db.GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName);
            var jobs = db.Db.GetCollection<BsonDocument>(db.WorkReportStatisticRebuildJobs.CollectionNamespace.CollectionName);
            var owner = new BsonDocument("_id", ObjectId.Parse(runId));
            if (mode == "missing-chunk") await artifacts.DeleteOneAsync(new BsonDocument("_id", headerId + "/0"));
            else if (mode == "chunk-data") await artifacts.UpdateOneAsync(new BsonDocument("_id", headerId + "/0"), new BsonDocument("$set", new BsonDocument("data", new BsonBinaryData(new byte[] { 1, 2, 3 }))));
            else if (mode == "extra-chunk") await artifacts.InsertOneAsync(new BsonDocument { ["_id"] = headerId + "/999", ["data"] = new BsonBinaryData(new byte[] { 0 }) });
            else if (mode == "pending") await artifacts.UpdateOneAsync(new BsonDocument("_id", headerId), new BsonDocument("$set", new BsonDocument("state", "STAGING")));
            else if (mode is "duplicate-manifest" or "unknown-manifest")
            {
                var header = await artifacts.Find(new BsonDocument("_id", headerId)).SingleAsync();
                var json = header["manifestJson"].AsString;
                json = "{" + (mode == "duplicate-manifest" ? "\"version\":2," : "\"unknown\":true,") + json[1..];
                var hash = StatRunCanonicalJson.HashText(json);
                await artifacts.UpdateOneAsync(new BsonDocument("_id", headerId), new BsonDocument("$set", new BsonDocument { ["manifestJson"] = json, ["manifestHash"] = hash }));
                await jobs.UpdateOneAsync(owner, new BsonDocument("$set", new BsonDocument("nativeStatisticPublication.manifestHash", hash)));
            }
            else if (mode == "missing-version") await jobs.UpdateOneAsync(owner, new BsonDocument("$unset", new BsonDocument("nativeStatisticPublication.version", "")));
            else if (mode == "pin-mismatch") await jobs.UpdateOneAsync(owner, new BsonDocument("$set", new BsonDocument("configHash", new string('b', 64))));
            else if (mode == "budget") await jobs.UpdateOneAsync(owner, new BsonDocument("$set", new BsonDocument("nativeStatisticPublication.bytes", 9 * 1024 * 1024)));
            else if (mode == "no-receipt") await jobs.UpdateOneAsync(owner, new BsonDocument("$unset", new BsonDocument("nativeStatisticPublication", "")));
            else throw new ArgumentException("Unknown fixture mode");
            return Results.Ok();
        }).RequireAuthorization();
        app.MapPost("/__canvas/artifact-snapshot/{runId}/{headerId}", async (string runId, string headerId, MongoDbContext db) =>
        {
            using var session = await db.Db.Client.StartSessionAsync();
            session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
            try
            {
                var job = await db.WorkReportStatisticRebuildJobs.Find(session, x => x.Id == runId).SingleAsync();
                await db.Db.GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName).UpdateOneAsync(new BsonDocument("_id", headerId + "/0"),
                    new BsonDocument("$set", new BsonDocument("data", new BsonBinaryData(new byte[] { 1, 2, 3 }))));
                _ = await NativeStatisticPublicationContract.ReadAsync(db.Db, job, default, session);
                var rejected = false;
                try { _ = await NativeStatisticPublicationContract.ReadAsync(db.Db, job, default); }
                catch (tdtd_be.Common.Errors.AppException) { rejected = true; }
                return new { snapshotRead = true, laterReadRejected = rejected };
            }
            finally { if (session.IsInTransaction) await session.AbortTransactionAsync(); }
        }).RequireAuthorization();
    }
}
