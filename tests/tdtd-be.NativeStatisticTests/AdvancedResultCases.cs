using System.Text.Json;
using System.Text.Json.Nodes;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.SummaryTokens;

namespace tdtd_be.NativeStatisticTests;

internal static class AdvancedResultCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        Task<Fixture> Create() => Fixture.Create(connection, database + "_advancedresult" + ++index);
        await test("Native range leap day and inclusive 366-day boundary", () => {
            var leap = AdvancedNativeRange.Bounds("2024-02-29..2024-02-29");
            Check(leap.StartUtc.Day == 29 && leap.EndExclusiveUtc.Month == 3 && leap.EndExclusiveUtc.Day == 1, "leap bounds");
            var year = AdvancedNativeRange.Bounds("2024-01-01..2024-12-31");
            Check((year.EndExclusiveUtc - year.StartUtc).Days == 366, "366-day bound");
            return Task.CompletedTask;
        });
        foreach (var key in new[] { "2026-02-29..2026-03-01", "2026-09-18..2026-09-17", "2024-01-01..2025-01-01", "9999-12-31..9999-12-31", "2026-9-17..2026-09-18", " 2026-09-17..2026-09-18" })
            await test("Advanced invalid RANGE " + key, async () => {
                var f = await Create();
                await Reject(() => f.Service().GetNativeSummaryAsync(f.Request("RANGE") with { GrainKey = key }, default), "ADVANCED_NATIVE_WINDOW_INVALID");
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "invalid range wrote snapshot");
                Check(await f.Inner.Ctx.WorkSummaryTokenLedgers.CountDocumentsAsync(l => l.RecordKind == WorkSummaryTokenLedgerRecordKinds.Entry
                    && l.TokenKind == WorkSummaryTokenKinds.AdvancedSummaryBroadHistoricalBuild) == 0, "invalid range charged quota");
            });
        await test("Advanced RANGE raw unequal AVG, exact history and registry", async () => {
            var f = await Create(); await f.AddReportOnFirstDay();
            var result = await f.Read("RANGE");
            var avg = result.Native.Groups.Single(g => g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1").Operations.Single(o => o.Method == "AVG").Numeric!;
            Check(avg.Sum == "12" && avg.Count == 3 && result.Native.Sources.Count == 3, "range averaged child means");
            Check(result.StartUtc == new DateTime(2026,9,17,0,0,0,DateTimeKind.Utc) && result.EndExclusiveUtc == new DateTime(2026,9,19,0,0,0,DateTimeKind.Utc), "range bounds lost");
            var locator = await f.Inner.Ctx.Db.GetCollection<BsonDocument>("canvas_native_storage_references_v1").Find(new BsonDocument("sourceId", result.SnapshotId)).SingleAsync();
            Check(locator["grain"] == "RANGE" && locator["grainKey"] == "2026-09-17..2026-09-18", "registry lost range");
            await f.Inner.Ctx.WorkAssignmentReports.UpdateManyAsync(_ => true, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
            var history = await f.Service().GetNativeSummaryAsync(f.Request("RANGE") with { Historical = true, SnapshotId = result.SnapshotId }, default);
            Check(history.SnapshotHash == result.SnapshotHash && history.Freshness == "HISTORICAL", "history rebased range");
        });
        await test("Advanced single-day RANGE excludes following day", async () => {
            var f = await Create(); var result = await f.Service().GetNativeSummaryAsync(f.Request("RANGE") with { GrainKey = "2026-09-17..2026-09-17" }, default);
            Check(result.Native.Sources.Count == 1 && result.QuotaLedgerId is not null, "single-day range membership/quota");
        });
        foreach (var grain in new[] { "DAY", "MONTH", "YEAR", "RANGE" })
            await test("Advanced raw " + grain + " result preserves exact numeric text and section scope", async () => {
                var f = await Create(); var result = await f.Read(grain);
                Check(result.Native.Sources.Count == (grain == "DAY" ? 1 : 2), "wrong raw membership");
                var avg = result.Native.Groups.Single(g => g.Address.TableId == "a" && g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1").Operations.Single(o => o.Method == "AVG").Numeric!;
                Check(avg.Sum == (grain == "DAY" ? "2" : "8") && avg.Count == (grain == "DAY" ? 1 : 2), "wrong AVG");
                Check(!result.Native.Groups.Any(g => g.Address.TableId == "records"), "other section leaked");
                Check(Canonical(result.Native).Contains("\\r\\n"), "CRLF lost");
                Check(result.LegacyFields.GetArrayLength() == 1 && result.TimeAxis == "UTC_GREGORIAN", "legacy/time axis omitted");
                var again = await f.Read(grain); Check(again.SnapshotHash == result.SnapshotHash && await f.Rows.CountDocumentsAsync(_ => true) == 1, "cache/replay changed capture");
                Check((grain == "DAY") == (result.QuotaLedgerId is null), "broad build quota omitted");
            });
        await test("Advanced concurrent broad build consumes once and stores one capture", async () => {
            var f = await Create(); var service = f.Service(); var rows = await Task.WhenAll(
                service.GetNativeSummaryAsync(f.Request("MONTH"), default), service.GetNativeSummaryAsync(f.Request("MONTH"), default));
            Check(rows[0].SnapshotHash == rows[1].SnapshotHash && await f.Rows.CountDocumentsAsync(_ => true) == 1, "concurrent conflict");
            Check(await f.Inner.Ctx.WorkSummaryTokenLedgers.CountDocumentsAsync(l => l.TokenKind == WorkSummaryTokenKinds.AdvancedSummaryBroadHistoricalBuild
                && l.RecordKind == WorkSummaryTokenLedgerRecordKinds.Entry) == 1, "quota duplicated");
        });
        await test("Advanced broad quota denial writes no snapshot", async () => {
            var f = await Create(); _ = await f.Read("MONTH");
            try { _ = await f.Read("YEAR"); throw new Exception("Expected quota denial"); }
            catch (AppException ex) { Check(ex.Code == AppErrorCode.WORK_SUMMARY_TOKEN_QUOTA_EXCEEDED, "wrong quota failure"); }
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "quota denial wrote snapshot");
        });
        await test("Advanced month uses raw reports despite bogus scalar child nodes", async () => {
            var f = await Create(); await f.Inner.Ctx.Db.GetCollection<BsonDocument>("work_assignment_advanced_summary_day_nodes")
                .InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "valueJson", "{\"wrongMean\":999}" } });
            var result = await f.Read("MONTH");
            Check(result.Native.Sources.Count == 2 && !Canonical(result.Native).Contains("wrongMean"), "child cache consumed");
        });
        await test("Advanced unequal daily counts keep raw AVG sum and count in month", async () => {
            var f = await Create(); await f.AddReportOnFirstDay();
            var day = await f.Read(); var month = await f.Read("MONTH");
            var daily = day.Native.Groups.Single(g => g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1").Operations.Single(o => o.Method == "AVG").Numeric!;
            var total = month.Native.Groups.Single(g => g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1").Operations.Single(o => o.Method == "AVG").Numeric!;
            Check(daily.Sum == "6" && daily.Count == 2 && total.Sum == "12" && total.Count == 3, "averaged daily averages or double-counted reports");
            Check(month.Native.Sources.Count == 3, "raw lineage lost");
        });
        await test("Advanced scan quota rejects before filtering out-of-window candidates", async () => {
            var f = await Create(); var collection = f.Inner.Ctx.Db.GetCollection<BsonDocument>(f.Inner.Ctx.WorkAssignmentReports.CollectionNamespace.CollectionName);
            var original = f.Inner.Reports[0].ToBsonDocument();
            var rows = Enumerable.Range(1000, 12001).Select(i => { var row = original.DeepClone().AsBsonDocument;
                row["_id"] = new ObjectId(i.ToString("x24")); row["periodKey"] = "2025-01-01";
                row["completedDate"] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc); return row; }).ToArray();
            await collection.InsertManyAsync(rows);
            await Reject(() => f.Read(), "ADVANCED_NATIVE_SOURCE_SCAN_QUOTA");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "truncated scan published");
        });
        await test("Advanced exact snapshot survives database restart", async () => {
            var f = await Create(); var before = await f.Read(); await restart(); var after = await f.Read();
            Check(before.SnapshotHash == after.SnapshotHash && Canonical(before.Native) == Canonical(after.Native), "restart changed result");
        });
        foreach (var fallback in new[] { "completed-precedence", "trimmed-period", "period-start", "report-date", "approved-date", "unresolvable" })
            await test("Advanced source day resolver " + fallback, async () => {
                var f = await Create(); var report = f.Inner.Reports[0];
                report.CompletedDate = null; report.PeriodKey = "bad"; report.PeriodStart = null; report.PeriodEnd = null;
                report.ReportDate = null; report.ApprovedAtUtc = null;
                var day = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);
                switch (fallback) {
                    case "completed-precedence": report.CompletedDate = day.AddDays(1); report.PeriodKey = "2026-09-17"; break;
                    case "trimmed-period": report.PeriodKey = " 2026-09-17 "; break;
                    case "period-start": report.PeriodStart = day; break;
                    case "report-date": report.ReportDate = day; break;
                    case "approved-date": report.ApprovedAtUtc = day; break;
                }
                await f.Inner.Ctx.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == report.Id, report);
                await f.Inner.Ctx.WorkReportPeriods.UpdateOneAsync(p => p.Id == report.WorkReportPeriodId,
                    Builders<WorkReportPeriod>.Update.Set(p => p.PeriodKey, report.PeriodKey).Set(p => p.PeriodStart, report.PeriodStart).Set(p => p.PeriodEnd, report.PeriodEnd));
                if (fallback == "unresolvable") await Reject(() => f.Read(), "ADVANCED_SUMMARY_SOURCE_DAY_UNRESOLVABLE");
                else Check((await f.Read()).Native.Sources.Count == (fallback == "completed-precedence" ? 0 : 1), "fallback membership incorrect");
            });
        await test("Advanced empty recalled set replaces current but preserves pinned history", async () => {
            var f = await Create(); var before = await f.Read();
            await f.Inner.Ctx.WorkAssignmentReports.UpdateManyAsync(_ => true, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
            await Reject(() => f.Service().GetNativeSummaryAsync(f.Request() with { SnapshotId = before.SnapshotId }, default), "ADVANCED_NATIVE_SNAPSHOT_STALE");
            Check((await f.Read()).Native.Sources.Count == 0, "recall kept old values");
            await f.Service().CreateNextP8DraftAsync(f.Root, f.Inner.Template.Id, "part-main", Command(f.Locked, new { }), default);
            var history = await f.Service().GetNativeSummaryAsync(f.Request() with { SnapshotId = before.SnapshotId, Historical = true }, default);
            Check(history.Freshness == "HISTORICAL" && history.SnapshotHash == before.SnapshotHash, "history rebased");
        });
        foreach (var fault in new[] { "period", "acl", "payload", "contribution", "config", "catalog", "flow" })
            await test("Advanced input drift refuses stale result: " + fault, async () => {
                var f = await Create(); var before = await f.Read();
                switch (fault) {
                    case "period": await f.Inner.Ctx.WorkReportPeriods.UpdateOneAsync(p => p.Id == f.Inner.Reports[0].WorkReportPeriodId, Builders<WorkReportPeriod>.Update.Set(p => p.CurrentReportId, "dddddddddddddddddddddddd")); break;
                    case "acl": await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[0].WorkAssignmentId, Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc").Set(a => a.Assignees, [])); break;
                    case "payload": await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id, Builders<WorkAssignmentReport>.Update.Set(r => r.PayloadHash, new string('0', 64))); break;
                    case "contribution": await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id, Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE")); break;
                    case "config": await f.Inner.Ctx.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.Inner.Template.Id, Builders<DynamicFormTemplate>.Update.Inc(t => t.StatisticConfigRevision, 1)); break;
                    case "catalog": f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogRawSha256 = new string('0', 64) }; break;
                    case "flow": await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Root, Builders<WorkAssignment>.Update.Set(a => a.FlowInstanceId, "dddddddddddddddddddddddd")); break;
                }
                await Reject(() => f.Service().GetNativeSummaryAsync(f.Request() with { SnapshotId = before.SnapshotId }, default));
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "drift wrote replacement");
            });
        foreach (var fault in new[] { "json", "hash", "bytes" })
            await test("Advanced corrupt snapshot " + fault + " retained", async () => {
                var f = await Create(); _ = await f.Read();
                await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set<BsonValue>(fault, fault == "bytes" ? new BsonInt32(1) : new BsonString("broken")));
                var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson(); await Reject(() => f.Read(), "ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw, "damaged raw overwritten");
            });
        await test("Advanced source mutation at transaction fence rolls back writes", async () => {
            var f = await Create(); async Task Drift() => await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id,
                Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
            await Reject(() => f.Service(before: Drift).GetNativeSummaryAsync(f.Request(), default), "ADVANCED_NATIVE_INPUT_CHANGED_RETRY");
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "fence failure persisted snapshot");
        });
        await test("Advanced unauthorized current and history cannot expose results", async () => {
            var f = await Create(); var before = await f.Read();
            await Reject(() => f.Service(actor: "cccccccccccccccccccccccc").GetNativeSummaryAsync(f.Request(), default));
            await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Inner.Reports[1].WorkAssignmentId,
                Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc").Set(a => a.Assignees, []));
            await Reject(() => f.Service().GetNativeSummaryAsync(f.Request() with { SnapshotId = before.SnapshotId, Historical = true }, default), "ADVANCED_SUMMARY_CONFIG_ACCESS_DENIED");
        });
        foreach (var invalid in new[] { "day", "WEEK", "9999" })
            await test("Advanced invalid time window " + invalid, async () => {
                var f = await Create(); await Reject(() => f.Service().GetNativeSummaryAsync(f.Request() with { Grain = invalid == "9999" ? "YEAR" : invalid, GrainKey = invalid }, default), "ADVANCED_NATIVE_WINDOW_INVALID");
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 0, "bad window persisted");
            });
        foreach (var workerGrain in new[] { "DAY", "RANGE" })
        await test("Advanced worker " + workerGrain + " queues then completes exact capture without HTTP context", async () => {
            var f = await Create(); var dispatcher = new Dispatcher(); var queued = await f.Service(jobs: dispatcher).QueueNativeRefreshAsync(f.Request(workerGrain), default);
            Check(dispatcher.Jobs.Single().Type == typeof(IWorkAssignmentAdvancedSummaryConfigService) && await f.Rows.CountDocumentsAsync(_ => true) == 0, "wrong dispatch/eager compute");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            var done = await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default);
            Check(done.State == "COMPLETED" && done.Attempt == 1 && done.SnapshotId == queued.SnapshotId, "worker receipt wrong");
            await restart(); await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            Check((await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default)).SnapshotHash == done.SnapshotHash, "worker replay drifted");
        });
        await test("Advanced ambiguous dispatch retains retryable pinned intent", async () => {
            var f = await Create(); await Reject(() => f.Service(jobs: new Dispatcher { Fail = true }).QueueNativeRefreshAsync(f.Request(), default), "ADVANCED_NATIVE_REFRESH_DISPATCH_UNCONFIRMED");
            var id = (await f.Jobs.Find(_ => true).SingleAsync())["_id"].AsString;
            await f.Service(jobs: new Dispatcher()).RetryNativeRefreshAsync(id, default);
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(id, default);
            Check((await f.Service().ReadNativeRefreshAsync(id, default)).State == "COMPLETED" && await f.Jobs.CountDocumentsAsync(_ => true) == 1, "retry duplicated intent");
        });
        foreach (var fault in new[] { "actor", "source", "catalog" })
            await test("Advanced queued worker rejects " + fault + " drift", async () => {
                var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
                if (fault == "actor") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, true));
                if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id, Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
                if (fault == "catalog") f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogRawSha256 = new string('0', 64) };
                await Reject(() => f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default));
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 0 && (await f.Jobs.Find(_ => true).SingleAsync())["state"] == "FAILED", "drift marked completed");
            });
        await test("Advanced worker acknowledgement interruption retains snapshot and resumes after restart", async () => {
            var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default); var count = 0;
            Task Interrupt() { if (++count == 2) throw new Interruption(); return Task.CompletedTask; }
            try { await f.Service(worker: true, before: Interrupt).RefreshNativeSnapshotJobAsync(queued.RefreshId, default); throw new Exception("Expected interrupt"); } catch (Interruption) { }
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 1 && (await f.Jobs.Find(_ => true).SingleAsync())["state"] == "FAILED", "partial receipt committed");
            var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson(); await restart();
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw && (await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default)).Attempt == 2, "retry replaced snapshot");
        });
        await test("Advanced lease prevents stale worker completion", async () => {
            var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            var store = new AdvancedNativeRefreshStore(f.Inner.Ctx.Db); var entry = await store.ReadAsync(queued.RefreshId, default);
            var lease = await store.ClaimAsync(entry, default); await Reject(() => store.ClaimAsync(entry, default), "ADVANCED_NATIVE_REFRESH_BUSY");
            await f.Jobs.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("leaseUntilUtc", DateTime.UtcNow.AddMinutes(-1)));
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            await store.FailAsync(lease, "late", default);
            Check((await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default)).State == "COMPLETED", "stale lease overwrote successor");
        });
        await test("Advanced completed receipt detects source recall and retains historical evidence", async () => {
            var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            await f.Inner.Ctx.WorkAssignmentReports.UpdateManyAsync(_ => true, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
            await Reject(() => f.Service().ReadNativeRefreshAsync(queued.RefreshId, default), "ADVANCED_NATIVE_SNAPSHOT_STALE");
            Check((await f.Jobs.Find(_ => true).SingleAsync())["state"] == "COMPLETED", "historical receipt erased");
        });
        await test("Advanced corrupt queued intent is rejected without repairing raw", async () => {
            var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            await f.Jobs.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", "broken"));
            var raw = (await f.Jobs.Find(_ => true).SingleAsync()).ToJson();
            await Reject(() => f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default), "ADVANCED_NATIVE_REFRESH_INTEGRITY");
            Check((await f.Jobs.Find(_ => true).SingleAsync()).ToJson() == raw, "raw intent repaired");
        });
        foreach (var fault in new[] { "lease", "source", "config", "catalog", "actor", "artifact" })
            await test("Advanced acknowledgement transaction rejects " + fault + " drift", async () => {
                var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default); var count = 0;
                async Task Drift()
                {
                    if (++count != 2) return;
                    if (fault == "lease") await f.Jobs.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("leaseUntilUtc", DateTime.UtcNow.AddMinutes(-1)));
                    if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id, Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
                    if (fault == "config") await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.UpdateOneAsync(_ => true, Builders<WorkAssignmentAdvancedSummaryConfig>.Update.Set(c => c.Status, "DRAFT"));
                    if (fault == "catalog") f.Inner.Activation.Binding = f.Inner.Activation.Binding with { CatalogSemanticSha256 = new string('b', 64) };
                    if (fault == "actor") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.Roles, ["SYSTEM_ADMIN"]));
                    if (fault == "artifact") await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", "broken"));
                }
                await Reject(() => f.Service(worker: true, before: Drift).RefreshNativeSnapshotJobAsync(queued.RefreshId, default));
                var row = await f.Jobs.Find(_ => true).SingleAsync();
                Check(row["state"] != "COMPLETED" && row["snapshotHash"].IsBsonNull && await f.Rows.CountDocumentsAsync(_ => true) == 1, "false completion or lost capture");
                var template = await f.Inner.Ctx.Db.GetCollection<BsonDocument>(f.Inner.Ctx.DynamicFormTemplates.CollectionNamespace.CollectionName).Find(_ => true).SingleAsync();
                Check(template["nativeStatisticPublicationFence"].ToInt64() == 1, "acknowledgement fences did not roll back");
            });
        await test("Advanced cancellation after claim preserves retryability without partial snapshot", async () => {
            var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            using var cancel = new CancellationTokenSource(); Task Cancel() { cancel.Cancel(); return Task.CompletedTask; }
            try { await f.Service(worker: true, before: Cancel).RefreshNativeSnapshotJobAsync(queued.RefreshId, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(await f.Rows.CountDocumentsAsync(_ => true) == 0 && (await f.Jobs.Find(_ => true).SingleAsync())["state"] == "FAILED", "cancelled partial commit");
            await f.Service(worker: true).RefreshNativeSnapshotJobAsync(queued.RefreshId, default);
            Check((await f.Service().ReadNativeRefreshAsync(queued.RefreshId, default)).State == "COMPLETED", "retry blocked");
        });
        await test("Advanced concurrent lease claims grant one owner", async () => {
            var f = await Create(); var queued = await f.Service(jobs: new Dispatcher()).QueueNativeRefreshAsync(f.Request(), default);
            var store = new AdvancedNativeRefreshStore(f.Inner.Ctx.Db); var entry = await store.ReadAsync(queued.RefreshId, default);
            async Task<bool> Claim() { try { await store.ClaimAsync(entry, default); return true; } catch (AppException) { return false; } }
            var claims = await Task.WhenAll(Claim(), Claim()); Check(claims.Count(c => c) == 1, "two owners claimed");
        });
    }

    private static string Canonical(object value) => StatConfigCanonicalJson.Canonicalize(value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string? reason = null)
    {
        try { await action(); }
        catch (AppException ex) { if (reason is not null) Check(JsonSerializer.Serialize(ex.Details).Contains(reason, StringComparison.Ordinal), "wrong rejection: " + JsonSerializer.Serialize(ex.Details)); return; }
        catch (tdtd_be.Services.WorkAssignmentReports.Statistics.WorkReportDirectGenerationValidationException) when (reason is null) { return; }
        throw new Exception("Expected structured rejection");
    }
    private static JsonElement Command(WorkAssignmentAdvancedSummaryConfigReadback state, object payload)
        => Fixtures.Json(new { commandId = Guid.NewGuid().ToString("N"), expectedRevision = state.Identity.Revision, expectedConfigHash = state.Identity.ConfigHash, payload });
    internal sealed class Fixture
    {
        internal required PublicationCases.Fixture Inner;
        internal string Root = "eeeeeeeeeeeeeeeeeeeeeeee";
        internal WorkAssignmentAdvancedSummaryConfigReadback Locked = null!;
        internal IMongoCollection<BsonDocument> Rows => Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeSnapshotStore.CollectionName);
        internal IMongoCollection<BsonDocument> Jobs => Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeRefreshStore.CollectionName);
        internal static async Task<Fixture> Create(string connection, string database, Func<tdtd_be.Data.MongoDbContext, Task<DynamicFormTemplate>>? configure = null)
        {
            var basic = await BasicResultCases.Fixture.Create(connection, database, "PERIOD_RANGE", "2026-09-17", "2026-09-18", configure);
            var f = new Fixture { Inner = basic.Inner };
            await f.Inner.Ctx.Units.InsertOneAsync(new Unit { Id = "bbbbbbbbbbbbbbbbbbbbbbbb", FullName = "Isolated unit", Code = "01" });
            await f.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == f.Root, Builders<WorkAssignment>.Update.Set(a => a.IssuedByUnitId, "bbbbbbbbbbbbbbbbbbbbbbbb"));
            var view = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(f.Inner.Template, f.Inner.Template.StatisticConfigId!,
                f.Inner.Template.StatisticConfigVersionId!, f.Inner.Template.StatisticConfigVersionNo, f.Inner.Template.StatisticConfigRevision, f.Inner.Template.StatisticConfigHash!);
            var refs = view.NativePlan!.Targets.Where(t => t.Configuration.TableId is "a" or "b")
                .SelectMany(t => t.Configuration.Operations!.Select(o => new WorkAssignmentAdvancedSummaryNativeTargetPayload(t.Configuration.TableId, t.Configuration.TargetId, o.OperationId))).ToArray();
            var state = await f.Service().GetP8ConfigAsync(f.Root, f.Inner.Template.Id, "part-main", default);
            var payload = new WorkAssignmentAdvancedSummaryConfigPayload(new("DIRECT_CHILDREN", null, null, null, null),
                [new("part-main", false, [new("root-count", "NUMBER", "SUM")], refs)], ["DAY", "MONTH", "YEAR"], [], [], "isolated Advanced native");
            var saved = await f.Service().PutP8ConfigAsync(f.Root, f.Inner.Template.Id, "part-main", Command(state, payload), default);
            f.Locked = await f.Service().LockP8ConfigAsync(f.Root, f.Inner.Template.Id, "part-main", Command(saved, new { }), default);
            return f;
        }
        internal AdvancedNativeSummaryRequest Request(string grain = "DAY") => new(Root, Inner.Template.Id, "part-main", grain,
            grain == "DAY" ? "2026-09-17" : grain == "MONTH" ? "2026-09" : grain == "RANGE" ? "2026-09-17..2026-09-18" : "2026");
        internal Task<AdvancedNativeSummaryResponse> Read(string grain = "DAY") => Service().GetNativeSummaryAsync(Request(grain), default);
        internal async Task AddReportOnFirstDay()
        {
            var original = Inner.Reports[0];
            var report = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WorkAssignmentReport>(original.ToBsonDocument());
            report.Id = 3.ToString("x24"); report.WorkAssignmentId = 301.ToString("x24"); report.WorkReportPeriodId = 401.ToString("x24");
            report.PeriodInstanceKey = "third-report-period"; report.PayloadRevision = 0;
            var payload = await Inner.Payload.LoadReportPayloadAsync(original);
            var json = JsonNode.Parse(payload.TableValuesJson!)!;
            var table = json["nativeTables"]!["tables"]!.AsArray().Single(t => t!["tableId"]!.GetValue<string>() == "a")!;
            foreach (var cell in table["rows"]![0]!["cells"]!.AsObject())
                if (cell.Value!["type"]!.GetValue<string>() == "number") cell.Value["value"] = 4;
            var saved = await Inner.Payload.SaveReportPayloadAsync(report, "{}", "{\"root-count\":4}", json.ToJsonString(), null, Inner.Actor, report.PayloadUpdatedAtUtc!.Value);
            report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash; report.PayloadSizeBytes = saved.PayloadSizeBytes; report.PayloadStatus = saved.PayloadStatus;
            var assignment = await Inner.Ctx.WorkAssignments.Find(a => a.Id == original.WorkAssignmentId).SingleAsync();
            assignment.Id = report.WorkAssignmentId;
            var period = await Inner.Ctx.WorkReportPeriods.Find(p => p.Id == original.WorkReportPeriodId).SingleAsync();
            period.Id = report.WorkReportPeriodId; period.WorkAssignmentId = report.WorkAssignmentId; period.PeriodInstanceKey = report.PeriodInstanceKey;
            period.CurrentReportId = report.Id; period.SourceLifecycleReportId = report.Id;
            await Inner.Ctx.WorkAssignments.InsertOneAsync(assignment); await Inner.Ctx.WorkReportPeriods.InsertOneAsync(period); await Inner.Ctx.WorkAssignmentReports.InsertOneAsync(report);
        }
        internal WorkAssignmentAdvancedSummaryConfigService Service(string? actor = null, IBackgroundJobClient? jobs = null,
            bool worker = false, Func<Task>? before = null, IHttpContextAccessor? httpAccessor = null)
        {
            var http = new DefaultHttpContext(); http.Items[MeAccessor.MeItemKey] = new MeResponse(actor ?? Inner.Actor, "fixture", "fixture", [], "bbbbbbbbbbbbbbbbbbbbbbbb", null, null, null, [], null, false);
            var runner = new StatConfigTransactionRunner(Inner.Ctx, NullLogger<StatConfigTransactionRunner>.Instance);
            return new(Inner.Ctx, jobs!, Inner.Payload, null!, new WorkSummaryTokenService(Inner.Ctx, runner),
                new(httpAccessor ?? new HttpContextAccessor { HttpContext = worker ? null : http }), new Hook(runner, before), Inner.Activation);
        }
    }
    private sealed class Hook(IStatConfigTransactionRunner inner, Func<Task>? before) : IStatConfigTransactionRunner
    {
        public async Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct = default)
        { if (before is not null) await before(); return await inner.ExecuteAsync(operation, ct); }
    }
    private sealed class Interruption : Exception { }
    private sealed class Dispatcher : IBackgroundJobClient
    {
        internal bool Fail;
        internal readonly List<Job> Jobs = [];
        public string Create(Job job, IState state) { Check(state is EnqueuedState, "not enqueued"); Jobs.Add(job); if (Fail) throw new Interruption(); return "fixture-" + Jobs.Count; }
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new Exception("Unexpected scheduler mutation");
    }
}
