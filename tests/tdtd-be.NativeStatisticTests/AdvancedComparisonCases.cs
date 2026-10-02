using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class AdvancedComparisonCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database)
    {
        var index = 0;
        Task<AdvancedResultCases.Fixture> Create() => AdvancedResultCases.Fixture.Create(connection, database + "_capturecompare" + ++index);
        static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        static async Task Reject(Func<Task> action, string? reason = null) {
            try { await action(); }
            catch (AppException ex) {
                if (reason is not null && !JsonSerializer.Serialize(ex.Details).Contains(reason)) throw;
                return;
            }
            throw new Exception("Expected rejection " + reason);
        }
        static async Task<string> Alter(AdvancedResultCases.Fixture f, Func<AdvancedNativeSnapshot, AdvancedNativeSnapshot> transform) {
            var row = await f.Rows.Find(_ => true).SingleAsync();
            var value = JsonSerializer.Deserialize<AdvancedNativeSnapshot>(row["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
            var text = StatConfigCanonicalJson.Canonicalize(transform(value)); var hash = StatRunCanonicalJson.HashText(text);
            await f.Rows.UpdateOneAsync(new BsonDocument("_id", row["_id"]), Builders<BsonDocument>.Update
                .Set("json", text).Set("hash", hash).Set("bytes", Encoding.UTF8.GetByteCount(text)));
            return hash;
        }
        foreach (var grain in new[] { "DAY", "RANGE" })
            await test("capture comparison " + grain + " independently recomputes and retains actual", async () => {
                var f = await Create(); var current = await f.Read(grain); var before = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                var ledgers = await f.Inner.Ctx.WorkSummaryTokenLedgers.CountDocumentsAsync(_ => true);
                var comparison = await f.Service().CompareNativeCurrentAsync(f.Request(grain) with { SnapshotId = current.SnapshotId }, current.SnapshotHash, default);
                Check(comparison.NativeComparison.Equivalent && comparison.SnapshotHash == current.SnapshotHash, "not equivalent");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == before, "comparison rewrote artifact");
                Check(await f.Inner.Ctx.WorkSummaryTokenLedgers.CountDocumentsAsync(_ => true) == ledgers, "comparison charged build quota");
            });
        foreach (var method in new[] { "SUM", "AVG", "CONCAT", "STACK_ROWS", "STACK_COLUMNS" })
            await test("capture comparison detects stored " + method + " error despite consistent envelope hash", async () => {
                var f = await Create(); var current = await f.Read("RANGE");
                var hash = await Alter(f, snapshot => {
                    var selected = snapshot.Native.Groups.First(g => g.Operations.Any(o => o.Method == method));
                    var operation = selected.Operations.First(o => o.Method == method);
                    var altered = method switch {
                        "SUM" => operation with { Value = operation.Value! with { Text = "999" } },
                        "AVG" => operation with { Numeric = operation.Numeric! with { Sum = "999" } },
                        "CONCAT" => operation with { TextChunks = [new(0, "wrong")] },
                        _ => operation with { Stack = operation.Stack! with { Rows = 999 } }
                    };
                    return snapshot with { Native = snapshot.Native with { Groups = snapshot.Native.Groups.Select(g => g == selected
                        ? g with { Operations = g.Operations.Select(o => o == operation ? altered : o).ToArray() } : g).ToArray() } };
                });
                var before = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                var result = await f.Service().CompareNativeCurrentAsync(f.Request("RANGE") with { SnapshotId = current.SnapshotId }, hash, default);
                Check(result.NativeComparison.Comparable && !result.NativeComparison.Equivalent
                    && result.NativeComparison.Differences.Any(d => d.Reason == "OPERATION_CHANGED"), "actual reused as expected");
                Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == before, "bad actual repaired");
            });
        await test("capture comparison does not hide unexpected target through display projection", async () => {
            var f = await Create(); var current = await f.Read();
            var hash = await Alter(f, s => s with { Native = s.Native with { Groups = s.Native.Groups.Append(s.Native.Groups[0] with { Address = s.Native.Groups[0].Address with { TargetId = "unexpected-hidden" } }).ToArray() } });
            await Reject(() => f.Service().CompareNativeCurrentAsync(f.Request() with { SnapshotId = current.SnapshotId }, hash, default), "NATIVE_RESULT_METADATA_OPERATION_MISMATCH");
        });
        await test("capture comparison rejects altered captured config even with unchanged result", async () => {
            var f = await Create(); var current = await f.Read();
            var hash = await Alter(f, s => s with { AdvancedConfigurationJson = "{}" });
            await Reject(() => f.Service().CompareNativeCurrentAsync(f.Request() with { SnapshotId = current.SnapshotId }, hash, default), "ADVANCED_NATIVE_COMPARISON_CAPTURE_BINDING_MISMATCH");
        });
        foreach (var fault in new[] { "history", "hash", "corrupt", "source", "actor", "foreign" })
            await test("capture comparison rejects " + fault, async () => {
                var f = await Create(); var current = await f.Read(); var request = f.Request() with { SnapshotId = current.SnapshotId };
                if (fault == "history") request = request with { Historical = true };
                if (fault == "corrupt") await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", new string('0', 64)));
                if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
                if (fault == "actor") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, true));
                await Reject(() => f.Service(actor: fault == "foreign" ? "cccccccccccccccccccccccc" : null)
                    .CompareNativeCurrentAsync(request, fault == "hash" ? new string('0', 64) : current.SnapshotHash, default));
                Check(await f.Rows.CountDocumentsAsync(_ => true) == 1, "failure created snapshot");
            });
        foreach (var fault in new[] { "source", "actor", "artifact", "membership", "config" })
            await test("capture comparison rejects concurrent " + fault + " drift", async () => {
                var f = await Create(); var current = await f.Read();
                async Task Drift() {
                    if (fault == "source") await f.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Inner.Reports[0].Id, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Submitted));
                    if (fault == "actor") await f.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == f.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, true));
                    if (fault == "artifact") await f.Rows.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Set("hash", new string('0', 64)));
                    if (fault == "membership") await f.AddReportOnFirstDay();
                    if (fault == "config") await f.Inner.Ctx.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.Inner.Template.Id, Builders<DynamicFormTemplate>.Update.Inc(t => t.StatisticConfigRevision, 1));
                }
                await Reject(() => f.Service(before: Drift).CompareNativeCurrentAsync(f.Request() with { SnapshotId = current.SnapshotId }, current.SnapshotHash, default));
            });
    }
}
