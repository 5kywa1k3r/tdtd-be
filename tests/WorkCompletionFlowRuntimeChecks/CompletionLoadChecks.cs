using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;

internal static class CompletionLoadChecks
{
    static string Id() => ObjectId.GenerateNewId().ToString();
    static T Copy<T>(T value) => BsonSerializer.Deserialize<T>(value!.ToBson());
    internal static async Task<bool> TryRunAsync(string mode, string path)
    {
        if (mode != "--close-load") return false;
        var m = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
        var database = m.GetProperty("database").GetString()!;
        var run = m.GetProperty("run").GetString()!;
        if (!database.StartsWith("p05_completion_flow_p05_flow_20261006_") || !run.StartsWith("p05-flow-20261006-")) throw new Exception("Own fixture only");
        var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = database }));
        var originalId = m.GetProperty("fixtures").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "ONCE").GetProperty("workId").GetString()!;
        var template = await db.Works.Find(w => w.Id == originalId).SingleAsync();
        if (!template.AutoCode.StartsWith(run)) throw new Exception("Fixture identity mismatch");
        var owner = await db.Users.Find(u => u.Id == template.Owner!.UserId).SingleAsync();
        var a0 = await db.WorkAssignments.Find(a => a.WorkId == originalId && a.ParentAssignmentId == null).SingleAsync();
        var b0 = await db.WorkTemplateAssignees.Find(b => b.WorkAssignmentId == a0.Id && b.IsActive && !b.IsDeleted).SingleAsync();
        var p0 = await db.WorkReportPeriods.Find(p => p.WorkAssignmentId == a0.Id && !p.IsDeleted).SingleAsync();
        var stamp = DateTime.UnixEpoch.AddMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var w = new Work { Id = Id(), AutoCode = run + "-close-load-" + Id(), Name = "P05 — phục hồi 166 nhánh ba cấp",
            Owner = Copy(template.Owner!), LeaderDirectiveUserId = owner.Id, CreatedByUserId = owner.Id,
            StartDate = template.StartDate, DueDate = template.DueDate, Status = WorkStatus.S3,
            CompletedAtUtc = stamp, CompletedDate = stamp.Date, CompletedByUserId = owner.Id, CompletionMode = "MANUAL", CompletionRevision = 1 };
        var nodes = new List<WorkAssignment>(); var bindings = new List<WorkTemplateAssignee>(); var periods = new List<WorkReportPeriod>();
        var expectedOpen = new Dictionary<string, bool>();
        for (var i = 0; i < 166; i++)
        {
            var user = new AppUser { Id = Id(), Username = w.AutoCode + "-" + i, FullName = "Người nhận kiểm tải " + i, UnitId = owner.UnitId, CreatedByUserId = owner.Id };
            await db.Users.InsertOneAsync(user);
            var parent = i < 10 ? null : nodes[i < 100 ? (i - 10) % 10 : 10 + (i - 100) % 90];
            var a = Copy(a0); a.Id = Id(); a.WorkId = w.Id; a.Name = "Nhánh kiểm tải " + i; a.Code = w.AutoCode + "-" + i;
            a.ParentAssignmentId = parent?.Id; a.RootAssignmentId = parent?.RootAssignmentId ?? a.Id; a.Path = (parent?.Path ?? "") + "/" + a.Id;
            a.Level = parent == null ? 0 : parent.Level + 1;
            a.Assignees = [new UserRef { UserId = user.Id, FullName = user.FullName, UnitId = user.UnitId }];
            a.IsActive = i == 0 || i % 41 != 0; a.CompletedAtUtc = i > 0 && i % 37 == 0 ? stamp : null;
            a.ProgressStatus = a.CompletedAtUtc.HasValue ? 2 : 0;
            a.CompletedDate = null; a.FirstApprovedAtUtc = null; a.CompletionReviewPeriodId = null; a.CompletionReopenedAtUtc = null;
            a.PendingCompletionRequestId = null; a.CompletionProjectionPending = false; a.CompletionMode = a.CompletedAtUtc.HasValue ? "MANUAL" : null;
            a.CompletionRevision = a.CompletedAtUtc.HasValue ? 1 : 0; a.CreatedByUserId = parent?.Assignees[0].UserId ?? owner.Id;
            a.CurrentReviewerUserId = a.CreatedByUserId;
            nodes.Add(a); expectedOpen[a.Id] = a.IsActive && !a.CompletedAtUtc.HasValue && (parent == null || expectedOpen[parent.Id]);
            var b = Copy(b0); b.Id = Id(); b.WorkId = w.Id; b.WorkAssignmentId = a.Id; b.AssigneeUserId = user.Id; b.IsActive = a.IsActive;
            bindings.Add(b);
            var p = Copy(p0); p.Id = Id(); p.WorkId = w.Id; p.WorkAssignmentId = a.Id; p.WorkTemplateAssigneeId = b.Id;
            p.AssigneeUserId = user.Id; p.CurrentReportId = null; p.PeriodInstanceKey = b.Id + ":ONCE"; p.PeriodKey = "ONCE";
            // This is an unreported obligation, not the approved historical period used as a schema template.
            p.Status = WorkReportPeriodStatus.Pending; p.IsHistoricalData = false; p.HistoricalDataApproved = false;
            p.HistoricalDataApprovedAtUtc = null; p.HistoricalDataApprovedByUserId = null;
            p.SourceLifecycleReportId = null; p.SourceLifecycleRevision = 0; p.SourceLifecycleAppliedAtUtc = null;
            p.ReportVersionCount = 0; p.LastDraftSavedAtUtc = null; p.LastSubmittedAtUtc = null; p.LastReviewedAtUtc = null;
            periods.Add(p);
        }
        await db.Works.InsertOneAsync(w); await db.WorkAssignments.InsertManyAsync(nodes);
        await db.WorkTemplateAssignees.InsertManyAsync(bindings); await db.WorkReportPeriods.InsertManyAsync(periods);
        var output = Environment.GetEnvironmentVariable("P05_CLOSE_OUTPUT") ?? throw new Exception("Output required");
        await File.WriteAllTextAsync(Path.Combine(output, "load-166-fixture.json"), JsonSerializer.Serialize(new { w.Id, database, expectedOpen }));
        await FixtureProvenanceChecks.Migration(db, "166 branches after final pins", default);
        var roles = new DocRoleReadModelProjectionService(db);
        await new DocRoleService(db, roles).UpsertWorkRootRolesAsync(w, default);
        await roles.RebuildWorkAsync(w.Id, owner.Id, default);
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5308/api/"), Timeout = TimeSpan.FromMinutes(3) };
        using var login = await http.PostAsJsonAsync("auth/login", new { username = owner.Username, password = Environment.GetEnvironmentVariable("P05_FLOW_FIXTURE_PASSWORD") ?? throw new Exception("Password required") });
        login.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString());
        var timer = Stopwatch.StartNew();
        using var res = await http.PostAsJsonAsync($"works/{w.Id}/completion/reopen", new { commandId = Id(), expectedRevision = 1, reason = "P05 kiểm phục hồi 166 nhánh ba cấp" });
        var body = await res.Content.ReadAsStringAsync(); var httpMs = timer.ElapsedMilliseconds;
        res.EnsureSuccessStatusCode();
        var state = JsonDocument.Parse(body).RootElement;
        if (state.GetProperty("projectionPending").GetBoolean()) throw new Exception("Load projection still pending: " + body);
        var expected = expectedOpen.Where(x => x.Value).Select(x => x.Key).ToHashSet();
        var jobs = await db.WorkAssignmentMaterializeJobs.Find(j => j.WorkId == w.Id && j.IsActive && !j.IsDeleted).ToListAsync();
        var queue = await db.WorkAssignmentQueueItems.Find(q => q.WorkId == w.Id && q.IsActive && !q.IsDeleted).ToListAsync();
        if (!expected.SetEquals(jobs.Select(j => j.WorkAssignmentId)) || !expected.SetEquals(queue.Select(q => q.WorkAssignmentId))) throw new Exception($"Restoration differs from expected ancestor/own locks: expected={expected.Count}, jobs={jobs.Count}, queue={queue.Count}, Work={w.Id}");
        var after = await db.WorkAssignments.Find(a => a.WorkId == w.Id).ToListAsync();
        if (after.Any(a => a.IsActive != nodes.Single(n => n.Id == a.Id).IsActive || a.CompletedAtUtc != nodes.Single(n => n.Id == a.Id).CompletedAtUtc)) throw new Exception("Child independent flags changed");
        if (await db.WorkReportPeriods.CountDocumentsAsync(p => p.WorkId == w.Id && !p.IsDeleted) != 166) throw new Exception("Duplicate periods");
        var results = new Dictionary<string, object>();
        foreach (var endpoint in new[] { $"works/{w.Id}/completion", $"works/{w.Id}", $"dashboard-mindmap/works/{w.Id}", $"dashboard/works/{w.Id}?forceRefresh=true" })
        {
            timer.Restart(); using var read = await http.GetAsync(endpoint); var json = await read.Content.ReadAsStringAsync();
            results[endpoint] = new { status = (int)read.StatusCode, ms = timer.ElapsedMilliseconds, value = JsonDocument.Parse(json).RootElement.Clone() };
            read.EnsureSuccessStatusCode();
            var value = JsonDocument.Parse(json).RootElement;
            if (endpoint.StartsWith("dashboard"))
            {
                var work = value.GetProperty("work");
                if (work.GetProperty("status").GetInt32() == 3 || work.GetProperty("activeRootAssignmentCount").GetInt32() != 10)
                    throw new Exception("Dashboard Work/root count differs after reopen");
                if (work.GetProperty("rootAssignmentProgressCounts").GetProperty("total").GetInt32() != 10)
                    throw new Exception("Dashboard denominator is not the 10 root branches");
                var page = value.GetProperty("rootAssignments");
                var roots = page.ValueKind == JsonValueKind.Array ? page : page.GetProperty("rows");
                var rootIds = nodes.Where(n => n.ParentAssignmentId == null).Select(n => n.Id).ToHashSet();
                if (!rootIds.SetEquals(roots.EnumerateArray().Select(row => (row.TryGetProperty("id", out var key) ? key : row.GetProperty("assignmentId")).GetString()!)))
                    throw new Exception("Dashboard root page contains non-root branches");
            }
        }
        await FixtureProvenanceChecks.Migration(db, "166 branches after reopen", default);
        await File.WriteAllTextAsync(Path.Combine(output, "load-166.json"), JsonSerializer.Serialize(new { workId = w.Id, database, branches = 166, maxDepth = 3,
            expectedActive = expected.Count, materializers = jobs.Count, queueItems = queue.Count, httpMs, periods = 166,
            childFlagsUnchanged = true, state = state.Clone(), results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS LOAD branches=166 depth=3 HTTP={httpMs}ms active={expected.Count} own/inherited locks preserved; no duplicate period; dashboard/tree reads HTTP 200");
        return true;
    }
}
