using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class BasicResultCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        async Task<Fixture> Create(string mode = "PERIOD_RANGE", string from = "2026-09-17", string to = "2026-09-18")
            => await Fixture.Create(connection, database + "_basicresult" + ++index, mode, from, to);
        await test("Basic native range calculates raw reports and stores full typed snapshot", async () =>
        {
            var f = await Create(); var result = await f.Read();
            var group = result.Native.Groups.Single(g => g.Address.TableId == "a" && g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1");
            var avg = group.Operations.Single(o => o.Method == "AVG").Numeric!;
            Check(avg.Sum == "8" && avg.Count == 2, "wrong raw AVG");
            Check(result.Native.Sources.Count == 2 && result.Freshness == "REVALIDATED", "missing sources or read state");
            var concat = result.Native.Groups.SelectMany(g => g.Operations).First(o => o.Method == "CONCAT");
            Check(string.Concat(concat.TextChunks!.Select(c => c.Text)).Contains("Việt 1\r\n🌿", StringComparison.Ordinal), "CONCAT content lost");
            Check(result.Native.Groups.SelectMany(g => g.Operations).Any(o => o.Stack is not null), "stack output lost");
            var replay = await f.Read(result.SnapshotId);
            Check(replay.SnapshotHash == result.SnapshotHash && Canonical(replay.Native) == Canonical(result.Native), "cached typed result changed");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "replay duplicated snapshot");
            Check(await f.Inner.Ctx.WorkAssignmentBasicSummarySnapshots.CountDocumentsAsync(_ => true) == 0, "native entered scalar cache");
        });
        await test("Basic native snapshot persists across actual Mongo restart", async () =>
        {
            var f = await Create(); var before = await f.Read(); await restart(); var after = await f.Read(before.SnapshotId);
            Check(after.SnapshotHash == before.SnapshotHash && Canonical(after.Native) == Canonical(before.Native), "restart changed result");
        });
        await test("Basic native concurrent exact captures persist one immutable snapshot", async () =>
        {
            var f = await Create(); var service = f.Service();
            var results = await Task.WhenAll(service.GetNativeSummaryAsync(f.Request(), default), service.GetNativeSummaryAsync(f.Request(), default));
            Check(results[0].SnapshotHash == results[1].SnapshotHash && await f.Rows.CountDocumentsAsync(_ => true) == 1, "concurrent result conflict");
        });
        foreach (var mode in new[] { "SINGLE_PERIOD", "ALL_PERIODS" })
            await test("Basic native locked period scope " + mode, async () =>
            {
                var f = await Create(mode);
                if (mode == "ALL_PERIODS") await f.Inner.Ctx.WorkAssignments.UpdateManyAsync(_ => true,
                    Builders<WorkAssignment>.Update.Set(a => a.AssignmentType, WorkAssignmentTypes.Once));
                var result = await f.Read();
                Check(result.Native.Sources.Count == (mode == "SINGLE_PERIOD" ? 1 : 2), "period membership mismatch");
            });
        await test("Basic native EXCLUDE changes capture and rejects previous snapshot ID", async () =>
        {
            var f = await Create(); var before = await f.Read();
            await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[1].Id,
                Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
            await Reject(() => f.Read(before.SnapshotId), "BASIC_NATIVE_SNAPSHOT_STALE");
            var after = await f.Read(); Check(after.Native.Sources.Count == 1 && before.SnapshotId != after.SnapshotId, "EXCLUDE reused old data");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 2, "old capture not retained");
        });
        await test("Basic native recall down to empty set does not retain old values", async () =>
        {
            var f = await Create(); var before = await f.Read();
            await f.Inner.Ctx.WorkAssignmentReports.UpdateManyAsync(_ => true,
                Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
            var after = await f.Read(); Check(after.Native.Sources.Count == 0 && after.SnapshotId != before.SnapshotId, "empty membership retained sources");
        });
        await test("Basic native pinned history keeps exact values after recall and next config draft", async () =>
        {
            var f = await Create(); var before = await f.Read();
            await f.Inner.Ctx.WorkAssignmentReports.UpdateManyAsync(_ => true,
                Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
            await f.Service().CreateNextP8DraftAsync(f.Root, f.Inner.Template.Id, Command(f.Locked, new { }), default);
            var historical = await f.Service().GetNativeSummaryAsync(f.Request(before.SnapshotId) with { Historical = true }, default);
            Check(historical.Freshness == "HISTORICAL" && historical.SnapshotHash == before.SnapshotHash
                && Canonical(historical.Native) == Canonical(before.Native), "history rebased or marked fresh");
        });
        await test("Basic native history requires exact identity and current full assignment rights", async () =>
        {
            var f = await Create(); var before = await f.Read();
            await Reject(() => f.Service().GetNativeSummaryAsync(f.Request() with { Historical = true }, default), "BASIC_NATIVE_HISTORY_EXACT_SNAPSHOT_REQUIRED");
            await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[1].WorkAssignmentId,
                Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc").Set(a => a.Assignees, []));
            await Reject(() => f.Service().GetNativeSummaryAsync(f.Request(before.SnapshotId) with { Historical = true }, default), "BASIC_SUMMARY_CONFIG_ACCESS_DENIED");
        });
        foreach (var fault in new[] { "access", "period", "schema", "contribution", "flow", "payload", "config", "catalog" })
            await test("Basic native cache rejects or changes after " + fault + " drift", async () =>
            {
                var f = await Create(); var before = await f.Read();
                if (fault == "access") await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[1].WorkAssignmentId,
                    Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc").Set(a => a.Assignees, []));
                if (fault == "period") await f.Inner.Ctx.WorkReportPeriods.UpdateOneAsync(p => p.Id == f.Inner.Reports[1].WorkReportPeriodId,
                    Builders<WorkReportPeriod>.Update.Inc(p => p.SourceLifecycleRevision, 1));
                if (fault == "schema") await f.Inner.Ctx.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.Inner.Template.Id,
                    Builders<DynamicFormTemplate>.Update.Set(t => t.PublishedSchemaHash, new string('0', 64)));
                if (fault == "contribution") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[1].Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, null));
                if (fault == "flow") await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[1].WorkAssignmentId,
                    Builders<WorkAssignment>.Update.Set(a => a.FlowInstanceId, "cccccccccccccccccccccccc"));
                if (fault == "payload") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[1].Id,
                    Builders<WorkAssignmentReport>.Update.Inc(r => r.PayloadRevision, 1));
                if (fault == "config") await f.Service().CreateNextP8DraftAsync(f.Root, f.Inner.Template.Id, Command(f.Locked, new { }), default);
                if (fault == "catalog") f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogSemanticSha256 = new string('b', 64) };
                await Reject(() => f.Read(before.SnapshotId));
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "drift wrote a replacement for pinned capture");
            });
        await test("Basic native input drift during calculation aborts snapshot write", async () =>
        {
            var f = await Create(); var hooked = new HookReader(f.Inner.Payload, async () =>
            {
                await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
            });
            await Reject(() => f.Service(reader: hooked).GetNativeSummaryAsync(f.Request(), default), "BASIC_NATIVE_INPUT_CHANGED_RETRY");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "drift persisted an unvalidated result");
        });
        foreach (var fault in new[] { "source", "config", "access", "period", "catalog" })
            await test("Basic native prewrite " + fault + " fence rolls back snapshot and counters", async () =>
            {
                var f = await Create();
                async Task Drift()
                {
                    if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[1].Id,
                        Builders<WorkAssignmentReport>.Update.Set(r => r.PayloadUpdatedAtUtc, DateTime.UtcNow));
                    if (fault == "config") await f.Inner.Ctx.WorkAssignmentBasicSummaryConfigs.UpdateOneAsync(_ => true,
                        Builders<WorkAssignmentBasicSummaryConfig>.Update.Set(c => c.Status, "DRAFT"));
                    if (fault == "access") await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[1].WorkAssignmentId,
                        Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc").Set(a => a.Assignees, []));
                    if (fault == "period") await f.Inner.Ctx.WorkReportPeriods.UpdateOneAsync(p => p.Id == f.Inner.Reports[1].WorkReportPeriodId,
                        Builders<WorkReportPeriod>.Update.Inc(p => p.SourceLifecycleRevision, 1));
                    if (fault == "catalog") f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogSemanticSha256 = new string('b', 64) };
                }
                await Reject(() => f.Service(beforeTransaction: Drift).GetNativeSummaryAsync(f.Request(), default));
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "prewrite drift persisted snapshot");
                var raw = f.Inner.Ctx.Db.GetCollection<BsonDocument>(f.Inner.Ctx.DynamicFormTemplates.CollectionNamespace.CollectionName);
                Check((await raw.Find(new BsonDocument("_id", ObjectId.Parse(f.Inner.Template.Id))).SingleAsync())
                    .GetValue("nativeStatisticPublicationFence", 0).ToInt64() == 0, "transaction fence did not roll back");
            });
        await test("Basic native postwrite membership change retains capture but returns no stale result", async () =>
        {
            var f = await Create();
            async Task Drift() => await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[1].Id,
                Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
            await Reject(() => f.Service(afterTransaction: Drift).GetNativeSummaryAsync(f.Request(), default), "BASIC_NATIVE_INPUT_CHANGED_RETRY");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "immutable captured evidence lost");
            Check((await f.Read()).Native.Sources.Count == 1, "retry did not use new membership");
        });
        await test("Basic native periodic ALL_PERIODS preserves required-window guard", async () =>
        {
            var f = await Create("ALL_PERIODS");
            await Reject(() => f.Read(), "BASIC_SUMMARY_PERIODIC_WINDOW_REQUIRED");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "invalid period scope wrote snapshot");
        });
        await test("Basic native snapshot size quota rejects without partial write", async () =>
        {
            var f = await Create(); var response = await f.Read(); var store = new BasicNativeSnapshotStore(f.Inner.Ctx.Db);
            var original = (await store.ReadAsync(response.SnapshotId, default))!.Value.Snapshot;
            var oversized = original with { SnapshotId = new string('c', 64), DefinitionJson = new string('x', BasicNativeSnapshotStore.MaxBytes + 1) };
            var runner = new StatConfigTransactionRunner(f.Inner.Ctx, NullLogger<StatConfigTransactionRunner>.Instance);
            await Reject(() => runner.ExecuteAsync(async (session, ct) => { await store.StoreAsync(session, oversized, ct); return true; }), "BASIC_NATIVE_SNAPSHOT_QUOTA");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "oversized snapshot partially persisted");
        });
        foreach (var fault in new[] { "hash", "bytes", "json", "extra" })
            await test("Basic native corrupted snapshot " + fault + " is retained and rejected", async () =>
            {
                var f = await Create(); var result = await f.Read();
                var update = fault switch { "hash" => new BsonDocument("hash", new string('0', 64)), "bytes" => new BsonDocument("bytes", 1),
                    "json" => new BsonDocument("json", "{}"), _ => new BsonDocument("unexpected", true) };
                await f.Rows.UpdateOneAsync(new BsonDocument("_id", result.SnapshotId), new BsonDocument("$set", update));
                var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Read(), "BASIC_NATIVE_SNAPSHOT_INTEGRITY");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw, "corrupt raw was repaired");
            });
        await test("Basic native denies unknown or unauthorized scope before snapshot lookup", async () =>
        {
            var f = await Create();
            await Reject(() => f.Service("cccccccccccccccccccccccc").GetNativeSummaryAsync(f.Request(), default), "BASIC_SUMMARY_CONFIG_ACCESS_DENIED");
            await Reject(() => f.Service().GetNativeSummaryAsync(new("dddddddddddddddddddddddd"), default), "BASIC_SUMMARY_CONFIG_ACCESS_DENIED");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "unauthorized result persisted");
        });
    }

    private static string Canonical(object value) => StatConfigCanonicalJson.Canonicalize(value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string? reason = null)
    {
        try { await action(); }
        catch (AppException ex) { if (reason is not null) Check(JsonSerializer.Serialize(ex.Details).Contains(reason, StringComparison.Ordinal), "wrong rejection: " + JsonSerializer.Serialize(ex.Details)); return; }
        catch (tdtd_be.Services.WorkAssignmentReports.Statistics.WorkReportDirectGenerationValidationException) when (reason is null) { return; }
        throw new Exception("Expected explicit rejection");
    }
    private static JsonElement Command(WorkAssignmentBasicSummaryConfigReadback state, object payload)
        => Fixtures.Json(new { commandId = Guid.NewGuid().ToString("N"), expectedRevision = state.Identity.Revision,
            expectedConfigHash = state.Identity.ConfigHash, payload });

    internal sealed class Fixture
    {
        internal required PublicationCases.Fixture Inner;
        internal string Root = "eeeeeeeeeeeeeeeeeeeeeeee";
        internal WorkAssignmentBasicSummaryConfigReadback Locked = null!;
        internal IMongoCollection<BsonDocument> Rows => Inner.Ctx.Db.GetCollection<BsonDocument>(BasicNativeSnapshotStore.CollectionName);
        internal static async Task<Fixture> Create(string connection, string database, string mode, string from, string to, Func<tdtd_be.Data.MongoDbContext, Task<DynamicFormTemplate>>? configure = null)
        {
            var f = new Fixture { Inner = await PublicationCases.Fixture.Create(connection, database, configure) };
            for (var i = 0; i < f.Inner.Reports.Length; i++)
            {
                var r = f.Inner.Reports[i]; r.PeriodKind = "SCHEDULED"; r.PeriodKey = $"2026-09-{17 + i}"; r.PeriodInstanceKey = "period-" + i;
                r.PeriodStart = new DateTime(2026, 9, 17 + i, 0, 0, 0, DateTimeKind.Utc); r.PeriodEnd = r.PeriodStart.Value.AddDays(1).AddTicks(-1);
                await f.Inner.Ctx.WorkAssignmentReports.ReplaceOneAsync(x => x.Id == r.Id, r);
                await f.Inner.Ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == r.WorkReportPeriodId,
                    Builders<WorkReportPeriod>.Update.Set(x => x.PeriodKind, r.PeriodKind).Set(x => x.PeriodKey, r.PeriodKey)
                        .Set(x => x.PeriodInstanceKey, r.PeriodInstanceKey).Set(x => x.PeriodStart, r.PeriodStart).Set(x => x.PeriodEnd, r.PeriodEnd));
                await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(x => x.Id == r.WorkAssignmentId,
                    Builders<WorkAssignment>.Update.Set(x => x.ParentAssignmentId, f.Root).Set(x => x.AssignmentType, WorkAssignmentTypes.PeriodicReport));
            }
            await f.Inner.Ctx.WorkAssignments.InsertOneAsync(new() { Id = f.Root, WorkId = f.Inner.Work, CreatedByUserId = f.Inner.Actor,
                IsActive = true, AssignmentType = WorkAssignmentTypes.PeriodicReport, DynamicFormTemplateId = f.Inner.Template.Id });
            var config = await f.Service().GetP8ConfigAsync(f.Root, f.Inner.Template.Id, default);
            var view = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(f.Inner.Template,
                f.Inner.Template.StatisticConfigId!, f.Inner.Template.StatisticConfigVersionId!, f.Inner.Template.StatisticConfigVersionNo,
                f.Inner.Template.StatisticConfigRevision, f.Inner.Template.StatisticConfigHash!);
            var refs = view.NativePlan!.Targets.SelectMany(t => t.Configuration.Operations!.Select(op =>
                new WorkAssignmentBasicSummaryNativeTargetPayload(t.Configuration.TableId, t.Configuration.TargetId, op.OperationId))).ToArray();
            var payload = new WorkAssignmentBasicSummaryConfigPayload(new("DIRECT_CHILDREN", null, null, null, null),
                new(mode, mode == "SINGLE_PERIOD" ? from : null, mode == "PERIOD_RANGE" ? from : null, mode == "PERIOD_RANGE" ? to : null),
                [], new(false, 12000), [], refs);
            var saved = await f.Service().PutP8ConfigAsync(f.Root, f.Inner.Template.Id, Command(config, payload), default);
            f.Locked = await f.Service().LockP8ConfigAsync(f.Root, f.Inner.Template.Id, Command(saved, new { }), default);
            return f;
        }
        internal BasicNativeSummaryRequest Request(string? id = null) => new(Root, Inner.Template.Id, SnapshotId: id);
        internal Task<BasicNativeSummaryResponse> Read(string? id = null) => Service().GetNativeSummaryAsync(Request(id), default);
        internal WorkAssignmentBasicSummaryService Service(string? actor = null, IWorkReportPayloadReader? reader = null,
            Func<Task>? beforeTransaction = null, Func<Task>? afterTransaction = null,
            Hangfire.IBackgroundJobClient? backgroundJobs = null, bool worker = false, IHttpContextAccessor? httpAccessor = null)
        {
            var http = new DefaultHttpContext();
            http.Items[MeAccessor.MeItemKey] = new MeResponse(actor ?? Inner.Actor, "fixture", "fixture", [], "bbbbbbbbbbbbbbbbbbbbbbbb", null, null, null, [], null, false);
            return new(Inner.Ctx, reader ?? Inner.Payload, null!, backgroundJobs!, null!, new(httpAccessor ?? new HttpContextAccessor { HttpContext = worker ? null : http }),
                new HookTransaction(new StatConfigTransactionRunner(Inner.Ctx, NullLogger<StatConfigTransactionRunner>.Instance),
                    beforeTransaction, afterTransaction), Inner.Activation);
        }
    }
    private sealed class HookTransaction(IStatConfigTransactionRunner inner, Func<Task>? before, Func<Task>? after) : IStatConfigTransactionRunner
    {
        public async Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct = default)
        {
            if (before is not null) await before();
            var value = await inner.ExecuteAsync(operation, ct);
            if (after is not null) await after();
            return value;
        }
    }
    private sealed class HookReader(IWorkReportPayloadReader inner, Func<Task> hook) : IWorkReportPayloadReader
    {
        private bool used;
        public async Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(WorkAssignmentReport report, CancellationToken ct = default)
        { var value = await inner.LoadReportPayloadAsync(report, ct); if (!used) { used = true; await hook(); } return value; }
        public Task<string?> LoadReportTableBlockAsync(WorkAssignmentReport report, string blockId, CancellationToken ct = default) => throw new Exception("Unexpected block read");
        public Task<IReadOnlyDictionary<string, string>> LoadReportTableBlocksAsync(WorkAssignmentReport report, IEnumerable<string> blockIds, CancellationToken ct = default) => throw new Exception("Unexpected blocks read");
    }
}
