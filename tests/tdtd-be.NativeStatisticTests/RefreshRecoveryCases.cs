using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class RefreshRecoveryCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        foreach (var kind in new[] { "Basic", "Advanced" })
        {
            Task<Fixture> Create() => Fixture.Create(kind, connection, database + "_recover_" + ++index);
            await test(kind + " recovery retains old request and intent canonical bytes", () => {
                var json = kind == "Basic" ? StatConfigCanonicalJson.Canonicalize(new BasicNativeSummaryRequest("scope"))
                    : StatConfigCanonicalJson.Canonicalize(new AdvancedNativeSummaryRequest("scope", "form", "part", "DAY", "2026-09-17"));
                Check(!json.Contains("commandId"), "null command changed legacy bytes");
                var intent = kind == "Basic" ? StatConfigCanonicalJson.Canonicalize(new BasicNativeRefreshIntent(1, "id", "actor", "hash", new("scope")))
                    : StatConfigCanonicalJson.Canonicalize(new AdvancedNativeRefreshIntent(1, "id", "actor", "hash", new("scope", "form", "part", "DAY", "2026-09-17")));
                Check(!intent.Contains("commandHash") && !intent.Contains("commandId"), "old intent bytes changed");
                return Task.CompletedTask;
            });
            await test(kind + " recovery finds command after lost response and Mongo restart", async () => {
                var f = await Create(); var first = await f.Queue("lost-response", false);
                await restart();
                Check((await f.Lookup("lost-response")).Id == first.Id, "lost durable identity");
                Check((await f.Queue("lost-response", false)).Id == first.Id && await f.Rows.CountDocumentsAsync(_ => true) == 1, "replay duplicated intent");
                await f.Worker(first.Id);
                var count = f.Dispatcher.Jobs.Count;
                Check((await f.Queue("lost-response", false)).State == "COMPLETED" && f.Dispatcher.Jobs.Count == count, "completed replay dispatched again");
            });
            await test(kind + " recovery same command cannot switch request", async () => {
                var f = await Create(); await f.Queue("same", false); var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Queue("same", true), "COMMAND_CONFLICT");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw && f.Dispatcher.Jobs.Count == 1, "conflict mutated original");
            });
            await test(kind + " recovery concurrent commands have one durable intent", async () => {
                var f = await Create(); var calls = await Task.WhenAll(f.Queue("concurrent", false), f.Queue("concurrent", false));
                Check(calls[0].Id == calls[1].Id && await f.Rows.CountDocumentsAsync(_ => true) == 1, "concurrent duplicate intent");
                await f.Worker(calls[0].Id); Check((await f.Lookup("concurrent")).State == "COMPLETED", "concurrent intent not usable");
            });
            await test(kind + " recovery ambiguous enqueue is found by caller command", async () => {
                var f = await Create(); f.Dispatcher.Fail = true;
                await Reject(() => f.Queue("enqueue-lost", false), "DISPATCH_UNCONFIRMED");
                var found = await f.Lookup("enqueue-lost"); Check(found.State == "QUEUED", "missing durable intent");
                f.Dispatcher.Fail = false; Check((await f.Queue("enqueue-lost", false)).Id == found.Id, "recovery changed ID");
            });
            await test(kind + " recovery rejects source drift and preserves original capture", async () => {
                var f = await Create(); var original = await f.Queue("drift", false);
                await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
                var row = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Queue("drift", false), "SNAPSHOT_STALE");
                Check((await f.Lookup("drift")).Id == original.Id && (await f.Rows.Find(_ => true).SingleAsync()).ToJson() == row, "stale recovery overwrote intent");
            });
            await test(kind + " recovery validates command syntax before writes", async () => {
                var f = await Create();
                foreach (var command in new[] { "", " ", "a/b", "a\nb", new string('a', 129) })
                    await Reject(() => f.Queue(command, false), "COMMAND_INVALID");
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 0 && f.Dispatcher.Jobs.Count == 0, "invalid command persisted");
            });
            await test(kind + " recovery corrupt intent remains raw", async () => {
                var f = await Create(); await f.Queue("corrupt", false);
                await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", "broken"));
                var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Lookup("corrupt"), "INTEGRITY"); await Reject(() => f.Queue("corrupt", false), "INTEGRITY");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw, "corrupt command repaired");
            });
            await test(kind + " recovery denies revoked actor and hides command from another actor", async () => {
                var f = await Create(); await f.Queue("private", false);
                await Reject(() => f.OtherLookup("private"), "NOT_FOUND");
                await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, true));
                await Reject(() => f.Lookup("private"), "ACTOR_UNAVAILABLE");
            });
            await test(kind + " denied replay cannot recreate a missing registry entry", async () => {
                var f = await Create(); await f.Queue("lost-access", false);
                var registry = f.Inner.Ctx.Db.GetCollection<BsonDocument>(NativeStorageReferenceStore.CollectionName);
                await registry.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty); // isolated legacy missing-entry fixture
                await f.Inner.Ctx.WorkAssignments.UpdateManyAsync(_ => true, Builders<WorkAssignment>.Update.Set(a => a.IsDeleted, true));
                var before = (await f.Rows.Find(_ => true).SingleAsync()).ToJson(); var denied = false;
                try { await f.Queue("lost-access", false); } catch (AppException) { denied = true; }
                Check(denied && await registry.CountDocumentsAsync(_ => true) == 0, "denied replay wrote registry");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == before && f.Dispatcher.Jobs.Count == 1, "denied replay mutated intent or dispatched");
            });
            await test(kind + " recovery running lease stays busy then expired lease can retry", async () => {
                var f = await Create(); var r = await f.Queue("lease", false);
                await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("state", "RUNNING")
                    .Set("leaseToken", "isolated-crashed-attempt").Set("leaseUntilUtc", DateTime.UtcNow.AddMinutes(10)).Set("attempt", 1));
                await Reject(() => f.Queue("lease", false), "BUSY");
                await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("leaseUntilUtc", DateTime.UtcNow.AddMinutes(-1)));
                Check((await f.Queue("lease", false)).Id == r.Id, "expired attempt changed identity");
                await f.Worker(r.Id); Check((await f.Lookup("lease")).State == "COMPLETED", "expired lease not recoverable");
            });
            await test(kind + " recovery cannot claim completion after source recall", async () => {
                var f = await Create(); var r = await f.Queue("recall", false); await f.Worker(r.Id);
                await f.Inner.Ctx.WorkAssignmentReports.UpdateManyAsync(_ => true, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
                await Reject(() => f.Lookup("recall"), "SNAPSHOT_STALE");
                await Reject(() => f.Queue("recall", false), "SNAPSHOT_STALE");
                Check((await f.Rows.Find(_ => true).SingleAsync())["state"] == "COMPLETED", "receipt erased");
            });
        }
        await test("refresh command identity separates actor and summary kind", () => {
            Check(new[] { NativeRefreshCommand.Id("BASIC", "a", "same"), NativeRefreshCommand.Id("BASIC", "b", "same"),
                NativeRefreshCommand.Id("ADVANCED", "a", "same") }.Distinct().Count() == 3, "identity collision"); return Task.CompletedTask;
        });
    }

    private sealed record Receipt(string Id, string State);
    private sealed class Fixture
    {
        internal required PublicationCases.Fixture Inner;
        internal required IMongoCollection<BsonDocument> Rows;
        internal required Func<string, bool, Task<Receipt>> Queue;
        internal required Func<string, Task<Receipt>> Lookup;
        internal required Func<string, Task<Receipt>> OtherLookup;
        internal required Func<string, Task> Worker;
        internal Dispatcher Dispatcher = new();
        internal static async Task<Fixture> Create(string kind, string connection, string database)
        {
            var jobs = new Dispatcher();
            if (kind == "Basic")
            {
                var f = await BasicResultCases.Fixture.Create(connection, database, "PERIOD_RANGE", "2026-09-17", "2026-09-18");
                var service = f.Service(backgroundJobs: jobs, httpAccessor: new FixedActor(f.Inner.Actor));
                return new() { Inner = f.Inner, Dispatcher = jobs, Rows = f.Inner.Ctx.Db.GetCollection<BsonDocument>(BasicNativeRefreshStore.CollectionName),
                    Queue = async (command, changed) => { var r = await service.QueueNativeRefreshAsync(f.Request() with { CommandId = command, SnapshotId = changed ? new string('a', 64) : null }, default); return new(r.RefreshId, r.State); },
                    Lookup = async command => { var r = await f.Service().ReadNativeRefreshByCommandAsync(command, default); return new(r.RefreshId, r.State); },
                    OtherLookup = async command => { var r = await f.Service("cccccccccccccccccccccccc").ReadNativeRefreshByCommandAsync(command, default); return new(r.RefreshId, r.State); },
                    Worker = id => f.Service(worker: true).RefreshNativeSnapshotJobAsync(id, default) };
            }
            var a = await AdvancedResultCases.Fixture.Create(connection, database);
            var advanced = a.Service(jobs: jobs, httpAccessor: new FixedActor(a.Inner.Actor));
            return new() { Inner = a.Inner, Dispatcher = jobs, Rows = a.Jobs,
                Queue = async (command, changed) => { var r = await advanced.QueueNativeRefreshAsync(a.Request() with { CommandId = command, GrainKey = changed ? "2026-09-18" : "2026-09-17" }, default); return new(r.RefreshId, r.State); },
                Lookup = async command => { var r = await a.Service().ReadNativeRefreshByCommandAsync(command, default); return new(r.RefreshId, r.State); },
                OtherLookup = async command => { var r = await a.Service("cccccccccccccccccccccccc").ReadNativeRefreshByCommandAsync(command, default); return new(r.RefreshId, r.State); },
                Worker = id => a.Service(worker: true).RefreshNativeSnapshotJobAsync(id, default) };
        }
    }
    // Explicit request context for concurrent service calls. An ambient
    // HttpContextAccessor set inside an async fixture factory does not flow back
    // to its caller; separate setters also clear each other's holder.
    private sealed class FixedActor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
        internal FixedActor(string id) => HttpContext!.Items[MeAccessor.MeItemKey] =
            new MeResponse(id, "fixture", "fixture", [], "bbbbbbbbbbbbbbbbbbbbbbbb", null, null, null, [], null, false);
    }
    internal sealed class Dispatcher : IBackgroundJobClient
    {
        internal bool Fail;
        internal readonly System.Collections.Concurrent.ConcurrentBag<Job> Jobs = [];
        public string Create(Job job, IState state) { Jobs.Add(job); if (Fail) throw new InvalidOperationException("isolated lost acknowledgement"); return Guid.NewGuid().ToString("N"); }
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new Exception("Unexpected scheduler mutation");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string reason)
    { try { await action(); } catch (AppException ex) { Check(JsonSerializer.Serialize(ex.Details).Contains(reason), "Wrong rejection: " + JsonSerializer.Serialize(ex.Details)); return; } throw new Exception("Expected " + reason); }
}
