using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Progress;

internal static class NoReportReopenChecks
{
    static string Id() => ObjectId.GenerateNewId().ToString();
    static T Copy<T>(T value) => BsonSerializer.Deserialize<T>(value.ToBson());
    static int passed;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + ++passed + " " + label); }

    internal static async Task<bool> TryRunAsync(string mode, string path)
    {
        if (mode != "--no-report-reopen") return false;
        var m = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        var database = m.GetProperty("database").GetString()!;
        var run = m.GetProperty("run").GetString()!;
        if (!database.StartsWith("p05_completion_flow_p05_flow_20261006_") || !run.StartsWith("p05-flow-20261006-")) throw new Exception("Own fixture only");
        var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = database }));
        var originalId = m.GetProperty("fixtures").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "ONCE").GetProperty("workId").GetString()!;
        var original = await db.Works.Find(w => w.Id == originalId).SingleAsync();
        if (!original.AutoCode.StartsWith(run)) throw new Exception("Fixture identity mismatch");
        var owner = await db.Users.Find(u => u.Id == original.Owner!.UserId).SingleAsync();
        var other = await db.Users.Find(u => u.Username == run + "-once-author").SingleAsync();
        var password = Environment.GetEnvironmentVariable("P05_FLOW_FIXTURE_PASSWORD") ?? throw new Exception("Password must be supplied in process environment");
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5308/api/"), Timeout = TimeSpan.FromSeconds(60) };
        async Task<string> Login(string user)
        {
            using var res = await http.PostAsJsonAsync("auth/login", new { username = user, password });
            res.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
        }
        var ownerToken = await Login(owner.Username); var otherToken = await Login(other.Username);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        async Task<JsonElement> Read(string id)
        {
            using var res = await http.GetAsync($"works/{id}/completion"); res.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        async Task<JsonElement> Reopen(string id, object command, int status, string label)
        {
            using var res = await http.PostAsJsonAsync($"works/{id}/completion/reopen", command);
            var body = await res.Content.ReadAsStringAsync();
            Check((int)res.StatusCode == status, label + " HTTP " + (int)res.StatusCode + (res.IsSuccessStatusCode ? "" : " " + body));
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        async Task<Work> NewWork(string label)
        {
            var w = new Work { Id = Id(), AutoCode = run + "-no-report-" + Id(), Name = "P05 — " + label,
                Owner = Copy(original.Owner!), LeaderDirectiveUserId = owner.Id, CreatedByUserId = owner.Id,
                StartDate = original.StartDate, DueDate = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
                Status = WorkStatus.S3, CompletedAtUtc = DateTime.UtcNow, CompletedDate = DateTime.UtcNow.Date,
                CompletedByUserId = owner.Id, CompletionMode = "MANUAL", CompletionRevision = 1 };
            await db.Works.InsertOneAsync(w);
            var projection = new DocRoleReadModelProjectionService(db);
            await new DocRoleService(db, projection).UpsertWorkRootRolesAsync(w, default);
            await projection.RebuildWorkAsync(w.Id, owner.Id, default);
            return w;
        }
        await FixtureProvenanceChecks.Migration(db, "before no-report fixture", default);
        var browser = await NewWork("mở lại nhiệm vụ chưa có báo cáo — kiểm giao diện");
        var empty = await NewWork("mở lại nhiệm vụ chưa có báo cáo — kiểm API");
        var read = await Read(empty.Id);
        Check(read.GetProperty("canReopen").GetBoolean() && !read.GetProperty("requiresReportSelection").GetBoolean(), "no-report capability explicitly allows reason-only");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        await Reopen(empty.Id, new { commandId = Id(), expectedRevision = 1, reason = "Thử sai người" }, 403, "non-owner rejected");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        await Reopen(empty.Id, new { commandId = Id(), expectedRevision = 0, reason = "Thử revision cũ" }, 409, "stale revision rejected");
        await Reopen(empty.Id, new { commandId = Id(), expectedRevision = 1, reason = " " }, 400, "empty reason rejected");
        var commandId = Id();
        var opened = await Reopen(empty.Id, new { commandId, expectedRevision = 1, reason = "Tiếp tục nhiệm vụ chưa có báo cáo" }, 200, "omitted period accepted");
        Check(!opened.GetProperty("completed").GetBoolean() && !opened.GetProperty("reopenHold").GetBoolean() && !opened.GetProperty("projectionPending").GetBoolean(), "open without fictional correction hold; projection settled");
        var replay = await Reopen(empty.Id, new { commandId, expectedRevision = 1, periodId = (string?)null, reason = "Tiếp tục nhiệm vụ chưa có báo cáo" }, 200, "same command replay with null period");
        Check(replay.GetProperty("revision").GetInt64() == 2, "replay does not add revision");
        await Reopen(empty.Id, new { commandId, expectedRevision = 1, reason = "Đổi nội dung cùng mã" }, 409, "changed command rejected");
        var saved = await db.Works.Find(w => w.Id == empty.Id).SingleAsync();
        Check(saved.DueDate == empty.DueDate && saved.CompletionReviewPeriodId == null && saved.CompletionReopenedByUserId == owner.Id, "deadline retained; BSON null period and actor roundtrip");
        Check(await db.WorkHistories.CountDocumentsAsync(h => h.WorkId == empty.Id) == 1, "exactly one decision history");

        // Existing reports, including hidden drafts, never qualify as absence. Only new private fixture IDs are modified.
        var originalState = await Read(original.Id);
        await Reopen(original.Id, new { commandId = Id(), expectedRevision = originalState.GetProperty("revision").GetInt64(), reason = "Không được bỏ chọn báo cáo" }, 409, "existing report requires selection");
        var f = await RuntimeFixture.Seed(db, run + "-no-report-race-" + Id(), default);
        var fw = await db.Works.Find(w => w.Id == f.WorkId).SingleAsync();
        fw.Owner = Copy(original.Owner!); fw.Status = WorkStatus.S3; fw.CompletedAtUtc = DateTime.UtcNow; fw.CompletedByUserId = owner.Id; fw.CompletionRevision = 1;
        await db.Works.ReplaceOneAsync(w => w.Id == fw.Id, fw);
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Report.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.IsActive, false));
        await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == f.Report.WorkReportPeriodId, Builders<WorkReportPeriod>.Update.Set(p => p.IsActive, false));
        var hidden = await Read(f.WorkId);
        Check(hidden.GetProperty("requiresReportSelection").GetBoolean() && !hidden.GetProperty("canReopen").GetBoolean(), "hidden draft is not no-report capability");
        var denied = await Reopen(f.WorkId, new { commandId = Id(), expectedRevision = 1, periodId = (string?)null, reason = "Báo cáo mới xuất hiện trước xác nhận" }, 409, "authoritative report recheck rejects null period");
        Check(denied.GetProperty("details").GetProperty("reason").GetString() == "WORK_REOPEN_REPORT_REQUIRED", "selection conflict has actionable reason");
        Check((await db.Works.Find(w => w.Id == f.WorkId).SingleAsync()).CompletionRevision == 1, "rejected command leaves Work unchanged");

        var flow = await NewWork("Flow giữ ranh giới");
        await db.Works.UpdateOneAsync(w => w.Id == flow.Id, Builders<Work>.Update.Set(w => w.AssignmentTopologyOwner, WorkAssignmentTopologyOwners.P5FlowRuntime));
        Check(!(await Read(flow.Id)).GetProperty("canReopen").GetBoolean(), "Flow capability stays closed");
        await Reopen(flow.Id, new { commandId = Id(), expectedRevision = 1, reason = "Flow không mở tại đây" }, 400, "Flow mutation stays closed");

        var tree = await NewWork("mở Work giữ khóa riêng nhánh con");
        var rootOriginal = await db.WorkAssignments.Find(a => a.WorkId == originalId && a.ParentAssignmentId == null).SingleAsync();
        var childOriginal = await db.WorkAssignments.Find(a => a.WorkId == originalId && a.ParentAssignmentId != null).SingleAsync();
        var root = Copy(rootOriginal); root.Id = Id(); root.WorkId = tree.Id; root.RootAssignmentId = root.Id; root.Path = "/" + root.Id;
        root.CompletedAtUtc = null; root.CompletionReviewPeriodId = null; root.CompletionReopenedAtUtc = null; root.CompletionMode = null;
        root.CompletionProjectionPending = false; root.FirstApprovedAtUtc = null; root.DueDate = tree.DueDate;
        var completedChild = Copy(childOriginal); completedChild.Id = Id(); completedChild.WorkId = tree.Id; completedChild.ParentAssignmentId = root.Id; completedChild.RootAssignmentId = root.Id;
        completedChild.Path = root.Path + "/" + completedChild.Id; completedChild.CompletedAtUtc = DateTime.UnixEpoch.AddMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); completedChild.CompletionReviewPeriodId = null; completedChild.CompletionProjectionPending = false;
        var inactiveChild = Copy(completedChild); inactiveChild.Id = Id(); inactiveChild.Path = root.Path + "/" + inactiveChild.Id; inactiveChild.IsActive = false; inactiveChild.CompletedAtUtc = null;
        await db.WorkAssignments.InsertManyAsync(new[] { root, completedChild, inactiveChild });
        foreach (var node in new[] { root, completedChild, inactiveChild })
        {
            var sourceId = node.Id == root.Id ? rootOriginal.Id : childOriginal.Id;
            var binding = Copy(await db.WorkTemplateAssignees.Find(b => b.WorkAssignmentId == sourceId && b.IsActive && !b.IsDeleted).SingleAsync());
            binding.Id = Id(); binding.WorkId = tree.Id; binding.WorkAssignmentId = node.Id; binding.DueDate = tree.DueDate;
            binding.IsActive = node.IsActive;
            await db.WorkTemplateAssignees.InsertOneAsync(binding);
        }
        await FixtureProvenanceChecks.Migration(db, "after final no-report fixture pins", default);
        var treeOpened = await Reopen(tree.Id, new { commandId = Id(), expectedRevision = 1, reason = "Mở cha, giữ khóa riêng con" }, 200, "no reports with child scopes accepted");
        Check(!treeOpened.GetProperty("projectionPending").GetBoolean(), "tree runtime restoration converges");
        Check((await db.WorkAssignments.Find(a => a.Id == completedChild.Id).SingleAsync()).CompletedAtUtc == completedChild.CompletedAtUtc &&
            !(await db.WorkAssignments.Find(a => a.Id == inactiveChild.Id).SingleAsync()).IsActive, "child own completion and deactivation unchanged");
        Check(await db.WorkAssignmentMaterializeJobs.Find(j => j.WorkAssignmentId == root.Id && j.IsActive && !j.IsDeleted).AnyAsync() &&
            !await db.WorkAssignmentMaterializeJobs.Find(j => (j.WorkAssignmentId == completedChild.Id || j.WorkAssignmentId == inactiveChild.Id) && j.IsActive && !j.IsDeleted).AnyAsync(), "only inherited-lock root materializer resumes");

        var storage = new MongoStorage(db.Db.Client, database, new MongoStorageOptions { Prefix = "p05_flow_fix",
            MigrationOptions = new() { MigrationStrategy = new MigrateMongoMigrationStrategy(), BackupStrategy = new CollectionMongoBackupStrategy() } });
        foreach (var id in new[] { empty.Id, tree.Id })
        {
            var job = new BackgroundJobClient(storage).Enqueue<WorkCompletionWorkflowService>(x => x.ReconcileWorkAsync(id, CancellationToken.None));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            while (true)
            {
                var row = await db.Db.GetCollection<BsonDocument>("p05_flow_fix.jobGraph").Find(new BsonDocument("_id", ObjectId.Parse(job))).SingleOrDefaultAsync(timeout.Token);
                var state = row?.GetValue("StateName", BsonNull.Value)?.ToString();
                if (state == "Succeeded") break;
                if (state == "Failed") throw new Exception("Probe job failed " + job);
                await Task.Delay(250, timeout.Token);
            }
            var stateAfter = await Read(id);
            Check(!stateAfter.GetProperty("completed").GetBoolean() && !stateAfter.GetProperty("reopenHold").GetBoolean(), "real reconcile job keeps incomplete no-report Work open " + job);
        }
        await FixtureProvenanceChecks.Migration(db, "after no-report API and jobs", default);
        var outDir = Environment.GetEnvironmentVariable("P05_NO_REPORT_OUTPUT") ?? throw new Exception("Output directory required");
        await File.WriteAllTextAsync(Path.Combine(outDir, "fixture.json"), JsonSerializer.Serialize(new { database, run, browserWorkId = browser.Id,
            emptyWorkId = empty.Id, treeWorkId = tree.Id, hiddenDraftWorkId = f.WorkId, flowWorkId = flow.Id,
            rootId = root.Id, completedChildId = completedChild.Id, inactiveChildId = inactiveChild.Id, passed }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("NO_REPORT_REOPEN_PASS " + passed);
        return true;
    }
}
