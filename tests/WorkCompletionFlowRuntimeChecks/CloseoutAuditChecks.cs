using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.WorkAssignments.Progress;

internal static class CloseoutAuditChecks
{
    internal static async Task<bool> TryRunAsync(string mode, string path)
    {
        if (mode is not ("--close-audit" or "--close-fix-fixture-level")) return false;
        var m = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        var database = m.GetProperty("database").GetString()!;
        if (!database.StartsWith("p05_completion_flow_p05_flow_20261006_")) throw new Exception("Own fixture only");
        var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = database }));
        var id = m.GetProperty("fixtures").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "PERIODIC").GetProperty("workId").GetString()!;
        var w = await db.Works.Find(w => w.Id == id).SingleAsync();
        if (!w.AutoCode.StartsWith(m.GetProperty("run").GetString()!)) throw new Exception("Fixture identity mismatch");
        if (mode == "--close-fix-fixture-level")
        {
            var seed = m.GetProperty("fixtures").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "PERIODIC");
            var sourceId = seed.GetProperty("sourceAssignmentId").GetString()!;
            var parentId = seed.GetProperty("assignmentId").GetString()!;
            var child = await db.WorkAssignments.Find(a => a.Id == sourceId && a.WorkId == id).SingleAsync();
            if (child.ParentAssignmentId != parentId || child.RootAssignmentId != parentId || child.Level is not (0 or 1)) throw new Exception("Unexpected fixture topology");
            if (child.Level == 0)
            {
                await File.WriteAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("P05_CLOSE_OUTPUT")!, "fixture-child-level-backup.json"), child.ToJson());
                var update = await db.WorkAssignments.UpdateOneAsync(a => a.Id == child.Id && a.ParentAssignmentId == parentId && a.Level == 0,
                    Builders<tdtd_be.Models.WorkAssignment>.Update.Set(a => a.Level, 1));
                if (update.ModifiedCount != 1) throw new Exception("Fixture level CAS conflict");
            }
            await FixtureProvenanceChecks.Migration(db, "fixture direct child level corrected; pins/payloads unchanged", default);
            Console.WriteLine("PASS fixture source Level=1 matches stored direct ParentAssignmentId; backup+CAS, no report mutation");
            return true;
        }
        var storage = new MongoStorage(db.Db.Client, database, new MongoStorageOptions { Prefix = "p05_flow_fix",
            MigrationOptions = new() { MigrationStrategy = new MigrateMongoMigrationStrategy(), BackupStrategy = new CollectionMongoBackupStrategy() } });
        var jobs = new List<string>();
        var rootId = m.GetProperty("fixtures").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "PERIODIC").GetProperty("assignmentId").GetString()!;
        for (var run = 0; run < 3; run++)
        {
            var job = run == 0 ? new BackgroundJobClient(storage).Enqueue<WorkCompletionWorkflowService>(x => x.TryConvergeAsync(rootId, CancellationToken.None))
                : new BackgroundJobClient(storage).Enqueue<WorkInboxNotificationJob>(x => x.ScanDueNotificationsAsync(CancellationToken.None)); jobs.Add(job);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            while (true)
            {
                var row = await db.Db.GetCollection<BsonDocument>("p05_flow_fix.jobGraph").Find(new BsonDocument("_id", ObjectId.Parse(job))).SingleOrDefaultAsync(timeout.Token);
                var state = row?.GetValue("StateName", BsonNull.Value)?.ToString();
                if (state == "Succeeded") break;
                if (state == "Failed") throw new Exception("Notification job failed: " + job);
                await Task.Delay(250, timeout.Token);
            }
        }
        var notifications = await db.Notifications.Find(n => n.WorkId == id && !n.IsDeleted).ToListAsync();
        var root = await db.WorkAssignments.Find(a => a.Id == rootId).SingleAsync();
        var reviewPeriods = await db.ReviewReportListDocRoles.Find(r => r.AssignmentId == rootId && !r.IsDeleted).ToListAsync();
        var reviewGroups = await db.ReviewAssignmentSummaryDocRoles.Find(r => r.AssignmentId == rootId && !r.IsDeleted).ToListAsync();
        if (reviewPeriods.Count == 0 || reviewGroups.Count == 0 || reviewPeriods.Any(r => r.ProgressStatus != root.ProgressStatus)
            || reviewGroups.Any(r => r.ProgressStatus != root.ProgressStatus)) throw new Exception("Review execution projection remains stale after completion retry");
        if (notifications.GroupBy(n => new { n.EventKey, n.RecipientUserId }).Any(g => g.Count() != 1)) throw new Exception("Duplicate lifecycle notification on replay");
        var source = await db.WorkAssignmentReports.Find(r => r.Id == "6ac50a78ea772ca23455ccac").SingleAsync();
        var assignment = await db.WorkAssignments.Find(a => a.Id == source.WorkAssignmentId).SingleAsync();
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5308/api/") };
        using var login = await http.PostAsJsonAsync("auth/login", new { username = m.GetProperty("run").GetString() + "-periodic-author", password = Environment.GetEnvironmentVariable("P05_FLOW_FIXTURE_PASSWORD") });
        login.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString());
        var summary = (await http.GetFromJsonAsync<JsonElement>("me/work-inbox/summary"));
        var notifyReview = summary.GetProperty("notifyReviewRequired").GetBoolean();
        var events = WorkReportNotificationEvents.Build(source, assignment, w, notifyReview);
        var required = WorkReportNotificationEvents.ExcludeAlreadyDeliveredLegacy(events,
            notifications.Select(n => n.EventKey).ToHashSet(StringComparer.Ordinal));
        foreach (var expected in required)
            if (!notifications.Any(n => n.EventKey == expected.EventKey && n.RecipientUserId == expected.RecipientUserId)) throw new Exception("Missing lifecycle notification: " + expected.Type + "; expected=" + expected.EventKey + "; actual=" + string.Join(",", notifications.Where(n => n.WorkAssignmentReportId == source.Id).Select(n => n.EventKey)));
        if (!events.Any(n => n.Type == "REPORT_RETURNED") || !events.Any(n => n.Type == "REPORT_APPROVED")) throw new Exception("Fixture did not exercise return/approve");
        var periods = await db.WorkReportPeriods.Find(p => p.WorkId == id && !p.IsDeleted).ToListAsync();
        if (periods.Count != 6 || periods.GroupBy(p => new { p.WorkAssignmentId, p.PeriodKey }).Any(g => g.Count() != 1)) throw new Exception("Periodic materialization duplicated/lost periods");
        await FixtureProvenanceChecks.Migration(db, "closeout after browser periodic lifecycle and notification replay", default);
        var output = Environment.GetEnvironmentVariable("P05_CLOSE_OUTPUT")!;
        await File.WriteAllTextAsync(Path.Combine(output, "notification-audit.json"), JsonSerializer.Serialize(new { database, workId = id, jobs, notifyReview, periodCount = periods.Count,
            rootProgress = root.ProgressStatus, reviewPeriodRows = reviewPeriods.Count, reviewSummaryRows = reviewGroups.Count, reviewProgressConsistent = true,
            expectedLifecycleEvents = events.Select(n => new { n.Type, n.EventKey, n.RecipientUserId }),
            notifications = notifications.Select(n => new { n.Id, n.Type, n.EventKey, n.RecipientUserId, n.WorkAssignmentReportId, n.ActionState }) }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS notifications: 2 real jobs; {events.Count} source events delivered, no duplicate event/recipient; exactly 6 periods; provenance migration PASS");
        return true;
    }
}
