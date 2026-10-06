using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Progress;

internal static class WorkReopenProbe
{
    internal static async Task<bool> TryRunAsync(string mode, string path)
    {
        if (mode is not ("--deadline-past" or "--deadline-restore" or "--reconcile-work" or "--audit-reopen")) return false;
        var m = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        var database = m.GetProperty("database").GetString()!;
        if (!database.StartsWith("p05_completion_flow_p05_flow_20261006_")) throw new Exception("Own fixture only");
        var id = m.GetProperty("fixtures").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "ONCE").GetProperty("workId").GetString()!;
        var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = database }));
        var w = await db.Works.Find(x => x.Id == id).SingleAsync();
        if (!w.AutoCode.StartsWith(m.GetProperty("run").GetString()!)) throw new Exception("Own Work only");
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "work-deadline-probe-backup.json");
        var past = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        if (mode == "--deadline-past")
        {
            if (File.Exists(backup))
            {
                var saved = JsonDocument.Parse(await File.ReadAllTextAsync(backup)).RootElement;
                if (saved.GetProperty("id").GetString() != id || saved.GetProperty("dueDate").GetDateTime() != w.DueDate)
                    throw new Exception("Restore previous probe first");
            }
            else await File.WriteAllTextAsync(backup, JsonSerializer.Serialize(new { id, dueDate = w.DueDate }));
            var changed = await db.Works.UpdateOneAsync(x => x.Id == id && x.DueDate == w.DueDate,
                Builders<Work>.Update.Set(x => x.DueDate, past));
            if (changed.ModifiedCount != 1) throw new Exception("Deadline probe CAS conflict");
        }
        else if (mode == "--deadline-restore")
        {
            var saved = JsonDocument.Parse(await File.ReadAllTextAsync(backup)).RootElement;
            if (saved.GetProperty("id").GetString() != id) throw new Exception("Backup mismatch");
            DateTime? due = saved.GetProperty("dueDate").ValueKind == JsonValueKind.Null ? null : saved.GetProperty("dueDate").GetDateTime();
            var changed = await db.Works.UpdateOneAsync(x => x.Id == id && x.DueDate == past, Builders<Work>.Update.Set(x => x.DueDate, due));
            if (changed.ModifiedCount != 1) throw new Exception("Restore CAS conflict");
            Console.WriteLine("Restored own fixture deadline; backup retained.");
        }
        else if (mode == "--reconcile-work")
        {
            var storage = new MongoStorage(db.Db.Client, database, new MongoStorageOptions { Prefix = "p05_flow_fix",
                MigrationOptions = new() { MigrationStrategy = new MigrateMongoMigrationStrategy(), BackupStrategy = new CollectionMongoBackupStrategy() } });
            var job = new BackgroundJobClient(storage).Enqueue<WorkCompletionWorkflowService>(x => x.ReconcileWorkAsync(id, CancellationToken.None));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            while (true)
            {
                var row = await db.Db.GetCollection<BsonDocument>("p05_flow_fix.jobGraph").Find(new BsonDocument("_id", ObjectId.Parse(job))).SingleOrDefaultAsync(timeout.Token);
                var state = row?.GetValue("StateName", BsonNull.Value)?.ToString();
                if (state == "Succeeded") break;
                if (state == "Failed") throw new Exception("Probe job failed: " + job);
                await Task.Delay(250, timeout.Token);
            }
            Console.WriteLine("JOB_SUCCEEDED " + job);
        }
        else
        {
            await FixtureProvenanceChecks.Migration(db, "after reopen/return/approve and real overdue jobs", default);
            var periods = await db.WorkReportPeriods.Find(p => p.WorkId == id && !p.IsDeleted).ToListAsync();
            if (periods.Count != 2 || periods.GroupBy(p => new { p.WorkAssignmentId, p.AssigneeUserId }).Any(g => g.Count() != 1))
                throw new Exception("Once periods duplicated during runtime restoration");
            if (w.CompletionProjectionPending || w.CompletionReviewPeriodId != null || !w.CompletedAtUtc.HasValue)
                throw new Exception("Work correction not fully settled");
            if (await db.WorkAssignmentQueueItems.Find(q => q.WorkId == id && q.IsActive && !q.IsDeleted).AnyAsync())
                throw new Exception("Completed Work still has an active queue item");
            if (await db.WorkAssignmentMaterializeJobs.Find(j => j.WorkId == id && j.IsActive && !j.IsDeleted).AnyAsync())
                throw new Exception("Completed Work still has an active materializer");
            Console.WriteLine("PASS provenance migration, exactly two original once periods, no active queue/materializer under completed Work, no pending projection.");
        }
        var after = await db.Works.Find(x => x.Id == id).SingleAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { after.Id, after.Status, after.CompletedAtUtc, after.CompletionReviewPeriodId, after.CompletionRevision, after.CompletionProjectionPending, after.DueDate }));
        return true;
    }
}
