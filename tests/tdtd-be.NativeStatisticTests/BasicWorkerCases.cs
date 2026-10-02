using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class BasicWorkerCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        async Task<BasicResultCases.Fixture> Create() => await BasicResultCases.Fixture.Create(connection,
            database + "_basicworker" + ++index, "PERIOD_RANGE", "2026-09-17", "2026-09-18");
        static IMongoCollection<BsonDocument> Jobs(BasicResultCases.Fixture f)
            => f.Inner.Ctx.Db.GetCollection<BsonDocument>(BasicNativeRefreshStore.CollectionName);
        await test("Basic concurrent same-command dispatch queues once and replays acknowledged job", async () =>
        {
            var f = await Create(); var dispatcher = new Dispatcher();
            var request = f.Request() with { CommandId = Guid.NewGuid().ToString("N") };
            // Fixture Service() replaces the ambient test HttpContext. Create it
            // once before starting either call so the second setup cannot clear it.
            var service = f.Service(backgroundJobs: dispatcher);
            var pair = await Task.WhenAll(service.QueueNativeRefreshAsync(request, default),
                service.QueueNativeRefreshAsync(request, default));
            Check(pair[0].RefreshId == pair[1].RefreshId && dispatcher.Jobs.Count == 1, "duplicate dispatch");
            var replay = await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(request, default);
            Check(replay.BackgroundJobId == "fixture-1" && dispatcher.Jobs.Count == 1, "queued replay dispatched again");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(replay.RefreshId, default);
            Check((await f.Service().ReadNativeRefreshAsync(replay.RefreshId, default)).Attempt == 1, "extra worker attempt");
        });
        await test("Basic dispatch lock excludes contenders and recovers expired owner", async () =>
        {
            var f = await Create(); var store = new BasicNativeRefreshStore(f.Inner.Ctx.Db);
            var id = ObjectId.GenerateNewId().ToString(); var first = await store.TryLockDispatchAsync(id, default);
            Check(first is not null && await store.TryLockDispatchAsync(id, default) is null, "lock did not exclude");
            var locks = f.Inner.Ctx.Db.GetCollection<BsonDocument>("work_assignment_basic_summary_native_dispatch_locks");
            await locks.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("untilUtc", DateTime.UtcNow.AddSeconds(-1))));
            var next = await store.TryLockDispatchAsync(id, default); Check(next is not null && next != first, "expired lock not recovered");
            await store.UnlockDispatchAsync(id, first!); Check(await store.TryLockDispatchAsync(id, default) is null, "old owner unlocked new owner");
            await store.UnlockDispatchAsync(id, next!); Check(await store.TryLockDispatchAsync(id, default) is not null, "release failed");
        });
        await test("Basic native queues existing Hangfire service and completes without HTTP context", async () =>
        {
            var f = await Create(); var dispatcher = new Dispatcher();
            var queued = await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(f.Request(), default);
            Check(queued.State == "QUEUED" && queued.BackgroundJobId == "fixture-1" && dispatcher.Jobs.Count == 1, "dispatch not recorded");
            var job = dispatcher.Jobs[0];
            Check(job.Type == typeof(IWorkAssignmentBasicSummaryService) && job.Method.Name == "RefreshNativeSnapshotJobAsync"
                && (string)job.Args[0] == queued.RefreshId && job.Args.Count == 2, "wrong worker or mutable inputs queued");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "queue computed result");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            var done = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            var result = await f.Read(done.SnapshotId);
            Check(done.State == "COMPLETED" && done.Attempt == 1 && done.SnapshotHash == result.SnapshotHash, "receipt not exact readback");
            var avg = result.Native.Groups.Single(g => g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1")
                .Operations.Single(o => o.Method == "AVG").Numeric!;
            Check(avg.Sum == "8" && avg.Count == 2, "worker changed raw AVG");
            Check(result.Native.Groups.SelectMany(g => g.Operations).Any(o => o.Method == "CONCAT" &&
                string.Concat(o.TextChunks!.Select(c => c.Text)).Contains("\r\n", StringComparison.Ordinal)), "worker lost CRLF");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(done.RefreshId, default);
            Check((await f.Service().ReadNativeRefreshAsync(done.RefreshId, default)).Attempt == 1, "replay recomputed");
            Check(await f.Inner.Ctx.WorkAssignmentBasicSummarySnapshots.CountDocumentsAsync(_ => true) == 0, "native job used scalar store");
        });
        await test("Basic native queued and completed receipts survive actual Mongo restart", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            await restart(); await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            var before = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            await restart(); var after = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            Check(before == after && before.State == "COMPLETED", "restart changed job receipt");
        });
        await test("Basic native ambiguous enqueue retains intent and retries same ID", async () =>
        {
            var f = await Create(); var dispatcher = new Dispatcher { Fail = true };
            try { await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(f.Request(), default); throw new Exception("Expected dispatch fault"); }
            catch (AppException ex)
            {
                using var detail = JsonDocument.Parse(JsonSerializer.Serialize(ex.Details));
                Check(detail.RootElement.GetProperty("reason").GetString() == "BASIC_NATIVE_REFRESH_DISPATCH_UNCONFIRMED"
                    && !string.IsNullOrWhiteSpace(detail.RootElement.GetProperty("refreshId").GetString()), "missing recoverable ID");
            }
            var raw = await Jobs(f).Find(_ => true).SingleAsync(); var id = raw["_id"].AsString;
            Check(raw["state"] == "QUEUED" && raw["attempt"] == 0, "lost dispatch intent");
            dispatcher.Fail = false;
            var retried = await f.Service(backgroundJobs: dispatcher).RetryNativeRefreshAsync(id, default);
            Check(retried.RefreshId == id && await Jobs(f).CountDocumentsAsync(_ => true) == 1, "retry created new intent");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(id, default);
            Check((await f.Service().ReadNativeRefreshAsync(id, default)).State == "COMPLETED", "retry did not complete");
        });
        await test("Basic accepted dispatch with lost ack survives restart and duplicate delivery", async () =>
        {
            var f = await Create(); var dispatcher = new Dispatcher { Fail = true };
            var request = f.Request() with { CommandId = Guid.NewGuid().ToString("N") };
            await Reject(() => f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(request, default),
                "BASIC_NATIVE_REFRESH_DISPATCH_UNCONFIRMED");
            Check(dispatcher.Jobs.Count == 1, "scheduler did not retain first delivery");
            var raw = await Jobs(f).Find(_ => true).SingleAsync(); var id = raw["_id"].AsString;
            var originalIntent = raw["json"].AsString;
            await restart(); dispatcher.Fail = false;
            var retry = await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(request, default);
            Check(retry.RefreshId == id && dispatcher.Jobs.Count == 2, "ambiguous retry changed intent");
            foreach (var job in dispatcher.Jobs)
                Check((string)job.Args[0] == id, "duplicate delivery changed capture identity");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(id, default);
            var done = await f.Service().ReadNativeRefreshAsync(id, default);
            var artifact = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(id, default);
            var replay = await f.Service().ReadNativeRefreshAsync(id, default);
            Check(done == replay && replay.Attempt == 1 && replay.State == "COMPLETED", "late delivery changed receipt");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1
                && (await f.Rows.Find(_ => true).SingleAsync()).ToJson() == artifact, "late delivery rewrote result");
            Check((await Jobs(f).Find(_ => true).SingleAsync())["json"].AsString == originalIntent, "retry repinned input");
        });
        await test("Basic failed attempt redispatch queues once and stale owner cannot fail successor", async () =>
        {
            var f = await Create(); var dispatcher = new Dispatcher();
            var request = f.Request() with { CommandId = Guid.NewGuid().ToString("N") };
            var queued = await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(request, default);
            var store = new BasicNativeRefreshStore(f.Inner.Ctx.Db);
            var lease = await store.ClaimAsync(await store.ReadAsync(queued.RefreshId, default), default);
            await store.FailAsync(lease, "CONTROLLED_TEST_FAILURE", default);
            var retry = await f.Service(backgroundJobs: dispatcher).RetryNativeRefreshAsync(queued.RefreshId, default);
            Check(retry.State == "QUEUED" && retry.ErrorCode is null && dispatcher.Jobs.Count == 2, "failed retry not requeued");
            await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(request, default);
            Check(dispatcher.Jobs.Count == 2, "queued recovery replay dispatched again");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            await store.FailAsync(lease, "LATE_OLD_FAILURE", default);
            var done = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            Check(done.State == "COMPLETED" && done.Attempt == 2 && done.ErrorCode is null, "old owner damaged recovery");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "recovery duplicated result");
        });
        foreach (var fault in new[] { "source", "config", "catalog", "actor-deleted", "actor-unit", "access" })
            await test("Basic native queued " + fault + " drift cannot complete", async () =>
            {
                var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
                if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[1].Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
                if (fault == "config") await f.Inner.Ctx.WorkAssignmentBasicSummaryConfigs.UpdateOneAsync(_ => true,
                    Builders<WorkAssignmentBasicSummaryConfig>.Update.Set(c => c.Status, "DRAFT"));
                if (fault == "catalog") f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogSemanticSha256 = new string('b', 64) };
                if (fault == "actor-deleted") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, true));
                if (fault == "actor-unit") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.UnitId, "cccccccccccccccccccccccc"));
                if (fault == "access") await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[1].WorkAssignmentId,
                    Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc").Set(a => a.Assignees, []));
                await Reject(() => f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default));
                var row = await Jobs(f).Find(_ => true).SingleAsync();
                Check(row["state"] == "FAILED" && row["snapshotHash"].IsBsonNull && await f.Rows.CountDocumentsAsync(_ => true) == 0,
                    "drift reported success or persisted a result");
            });
        await test("Basic native live lease excludes duplicate worker and expired lease resumes", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            var store = new BasicNativeRefreshStore(f.Inner.Ctx.Db);
            var lease = await store.ClaimAsync(await store.ReadAsync(queued.RefreshId, default), default);
            await Reject(() => f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default), "BASIC_NATIVE_REFRESH_BUSY");
            Check((await store.ReadAsync(queued.RefreshId, default)).State == "RUNNING", "duplicate failed valid owner");
            await Jobs(f).UpdateOneAsync(new BsonDocument("_id", queued.RefreshId), Builders<BsonDocument>.Update.Set("leaseUntilUtc", DateTime.UtcNow.AddMinutes(-1)));
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            await store.FailAsync(lease, "OLD_ATTEMPT", default);
            var done = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            Check(done.State == "COMPLETED" && done.Attempt == 2 && done.ErrorCode is null, "old attempt corrupted successor");
        });
        await test("Basic native concurrent claims grant exactly one attempt", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            var store = new BasicNativeRefreshStore(f.Inner.Ctx.Db); var entry = await store.ReadAsync(queued.RefreshId, default);
            async Task<bool> Claim() { try { await store.ClaimAsync(entry, default); return true; } catch (AppException) { return false; } }
            var outcomes = await Task.WhenAll(Claim(), Claim());
            Check(outcomes.Count(x => x) == 1 && (await store.ReadAsync(queued.RefreshId, default)).Response.Attempt == 1, "two lease owners");
        });
        foreach (var fault in new[] { "lease", "source", "config", "catalog", "actor", "artifact" })
            await test("Basic native completion transaction rejects " + fault + " drift", async () =>
            {
                var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
                var transactions = 0;
                async Task Drift()
                {
                    if (++transactions != 2) return; // First transaction stores the capture; second acknowledges the attempt.
                    if (fault == "lease") await Jobs(f).UpdateOneAsync(new BsonDocument("_id", queued.RefreshId),
                        Builders<BsonDocument>.Update.Set("leaseUntilUtc", DateTime.UtcNow.AddMinutes(-1)));
                    if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id,
                        Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
                    if (fault == "config") await f.Inner.Ctx.WorkAssignmentBasicSummaryConfigs.UpdateOneAsync(_ => true,
                        Builders<WorkAssignmentBasicSummaryConfig>.Update.Set(c => c.Status, "DRAFT"));
                    if (fault == "catalog") f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogSemanticSha256 = new string('b', 64) };
                    if (fault == "actor") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor,
                        Builders<AppUser>.Update.Set(u => u.Roles, ["SYSTEM_ADMIN"]));
                    if (fault == "artifact") await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", "broken"));
                }
                await Reject(() => f.Service(beforeTransaction: Drift, worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default));
                var row = await Jobs(f).Find(_ => true).SingleAsync();
                Check(row["state"] != "COMPLETED" && row["snapshotHash"].IsBsonNull, "ack survived changed input");
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "lost retained capture evidence");
                var template = await f.Inner.Ctx.Db.GetCollection<BsonDocument>(f.Inner.Ctx.DynamicFormTemplates.CollectionNamespace.CollectionName)
                    .Find(new BsonDocument("_id", ObjectId.Parse(f.Inner.Template.Id))).SingleAsync();
                Check(template["nativeStatisticPublicationFence"].ToInt64() == 1, "failed ack leaked source fence increment");
            });
        foreach (var fault in new[] { "json", "hash", "extra", "state" })
            await test("Basic native corrupt refresh " + fault + " remains raw", async () =>
            {
                var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
                var update = fault switch { "json" => new BsonDocument("json", "{}"), "hash" => new BsonDocument("hash", "wrong"),
                    "state" => new BsonDocument("state", "DONE"), _ => new BsonDocument("unknown", true) };
                await Jobs(f).UpdateOneAsync(new BsonDocument("_id", queued.RefreshId), new BsonDocument("$set", update));
                var raw = (await Jobs(f).Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default), "BASIC_NATIVE_REFRESH_INTEGRITY");
                Check((await Jobs(f).Find(_ => true).SingleAsync()).ToJson() == raw, "corrupt job repaired");
            });
        await test("Basic native completed refresh refuses corrupt artifact without rebuilding", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", "broken"));
            var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
            await Reject(() => f.Service().ReadNativeRefreshAsync(queued.RefreshId, default), "BASIC_NATIVE_SNAPSHOT_INTEGRITY");
            await Reject(() => f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default), "BASIC_NATIVE_SNAPSHOT_INTEGRITY");
            Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw, "completed artifact repaired");
        });
        await test("Basic native completed receipt never masks later recall as current", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id,
                Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
            await Reject(() => f.Service().ReadNativeRefreshAsync(queued.RefreshId, default), "BASIC_NATIVE_SNAPSHOT_STALE");
            Check((await Jobs(f).Find(_ => true).SingleAsync())["state"] == "COMPLETED", "past receipt overwritten");
            var historical = await f.Service().GetNativeSummaryAsync(f.Request(queued.SnapshotId) with { Historical = true }, default);
            Check(historical.Freshness == "HISTORICAL" && historical.Native.Sources.Count == 2, "history rebased");
        });
        await test("Basic native job status hides another actor and rejects historical refresh", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            await Reject(() => f.Service("cccccccccccccccccccccccc").ReadNativeRefreshAsync(queued.RefreshId, default), "BASIC_NATIVE_REFRESH_NOT_FOUND");
            await Reject(() => f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request() with { Historical = true }, default), "BASIC_NATIVE_REFRESH_CURRENT_REQUIRED");
            Check(await Jobs(f).CountDocumentsAsync(_ => true) == 1, "invalid request persisted");
        });
        await test("Basic native interrupted acknowledgement resumes retained capture after restart", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            var transactions = 0;
            Task Interrupt() { if (++transactions == 2) throw new DispatchFault(); return Task.CompletedTask; }
            try { await f.Service(beforeTransaction: Interrupt, worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default); throw new Exception("Expected interruption"); }
            catch (DispatchFault) { }
            Check((await Jobs(f).Find(_ => true).SingleAsync())["state"] == "FAILED" && await f.Rows.CountDocumentsAsync(_ => true) == 1,
                "interrupted acknowledgement lost capture or marked success");
            var original = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
            await restart();
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            var done = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            Check(done.State == "COMPLETED" && done.Attempt == 2 && (await f.Rows.Find(_ => true).SingleAsync()).ToJson() == original,
                "retry replaced exact capture");
        });
        await test("Basic native cancellation after claim marks failed without partial result", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            using var cancel = new CancellationTokenSource();
            Task Cancel() { cancel.Cancel(); return Task.CompletedTask; }
            try { await f.Service(beforeTransaction: Cancel, worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            var row = await Jobs(f).Find(_ => true).SingleAsync();
            Check(row["state"] == "FAILED" && row["errorCode"] == "BASIC_NATIVE_REFRESH_CANCELLED"
                && await f.Rows.CountDocumentsAsync(_ => true) == 0, "cancellation left partial publication");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            Check((await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default)).State == "COMPLETED", "cancelled lease could not resume");
        });
        await test("Basic native post-ack source drift refuses success response and preserves historical receipt", async () =>
        {
            var f = await Create(); var queued = await f.Service(backgroundJobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            var transactions = 0;
            async Task Drift()
            {
                if (++transactions == 2) await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
            }
            await Reject(() => f.Service(afterTransaction: Drift, worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default), "BASIC_NATIVE_SNAPSHOT_STALE");
            Check((await Jobs(f).Find(_ => true).SingleAsync())["state"] == "COMPLETED", "immutable completion evidence overwritten");
            await Reject(() => f.Service().ReadNativeRefreshAsync(queued.RefreshId, default), "BASIC_NATIVE_SNAPSHOT_STALE");
        });
        await test("Basic native cancelled worker writes no result and is retryable", async () =>
        {
            var f = await Create(); var dispatcher = new Dispatcher();
            var queued = await f.Service(backgroundJobs: dispatcher).QueueNativeRefreshAsync(f.Request(), default);
            try { await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, new CancellationToken(true)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "cancelled worker wrote artifact");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            Check((await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default)).State == "COMPLETED", "cancelled request could not retry");
        });
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string? reason = null)
    {
        try { await action(); }
        catch (AppException ex)
        {
            if (reason is not null) Check(JsonSerializer.Serialize(ex.Details).Contains(reason, StringComparison.Ordinal), "wrong rejection: " + JsonSerializer.Serialize(ex.Details));
            return;
        }
        throw new Exception("Expected structured rejection");
    }
    private sealed class DispatchFault : Exception { }
    private sealed class Dispatcher : IBackgroundJobClient
    {
        internal bool Fail;
        internal readonly List<Job> Jobs = [];
        public string Create(Job job, IState state)
        {
            Check(state is EnqueuedState, "not enqueued"); Jobs.Add(job);
            if (Fail) throw new DispatchFault();
            return "fixture-" + Jobs.Count;
        }
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new Exception("Unexpected scheduler mutation");
    }
}
