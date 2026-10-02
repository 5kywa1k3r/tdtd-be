using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Labels;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.NativeStatisticTests;

internal static class NativeLabelCases
{
    private const string Unit = "bbbbbbbbbbbbbbbbbbbbbbbb";
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        foreach (var kind in new[] { "Basic", "Advanced" })
        {
            async Task<Fixture> Create()
            {
                var state = new LabelState();
                var name = database + "_labels_" + ++index;
                if (kind == "Basic")
                {
                    var b = await BasicResultCases.Fixture.Create(connection, name, "PERIOD_RANGE", "2026-09-17", "2026-09-18", state.Configure);
                    return new(b.Inner, state, async id => JsonSerializer.Serialize(await b.Service().GetNativeSummaryAsync(b.Request(id) with { Historical = id is not null }, default)),
                        async () => { var r = await b.Service(backgroundJobs: new RefreshRecoveryCases.Dispatcher()).QueueNativeRefreshAsync(b.Request(), default); await b.Service(worker: true).RefreshNativeSnapshotJobAsync(r.RefreshId, default); return (await b.Service().ReadNativeRefreshAsync(r.RefreshId, default)).State; });
                }
                var a = await AdvancedResultCases.Fixture.Create(connection, name, state.Configure);
                return new(a.Inner, state, async id => JsonSerializer.Serialize(await a.Service().GetNativeSummaryAsync(a.Request() with { Historical = id is not null, SnapshotId = id }, default)),
                    async () => { var r = await a.Service(jobs: new RefreshRecoveryCases.Dispatcher()).QueueNativeRefreshAsync(a.Request(), default); await a.Service(worker: true).RefreshNativeSnapshotJobAsync(r.RefreshId, default); return (await a.Service().ReadNativeRefreshAsync(r.RefreshId, default)).State; });
            }
            await test(kind + " native STATISTIC label passes real P8 config, result, worker and restart", async () => {
                var f = await Create(); var before = await f.Read(null); var snapshot = SnapshotId(before);
                var stored = await f.Inner.Ctx.Db.GetCollection<BsonDocument>("work_assignment_" + kind.ToLowerInvariant() + "_summary_native_snapshots")
                    .Find(new BsonDocument("_id", snapshot)).SingleAsync();
                Check(stored["json"].AsString.Contains("metric.amount"), "snapshot lost selected label metadata");
                Check(await f.Worker() == "COMPLETED", "label blocked worker");
                await restart(); Check(SnapshotId(await f.Read(null)) == snapshot, "readback changed capture");
                Check((await f.Read(snapshot)).Contains("HISTORICAL"), "history lost");
                var view = View(f.Inner.Template); var label = view.NativePlan!.Targets.Single(t => t.Configuration.TargetId == "a-legacy").LabelSnapshots.Single();
                var owner = await f.Inner.Ctx.Labels.Find(l => l.Id == label.LabelId).SingleAsync();
                LabelConfigCommandService.ValidateTrustedStatisticSnapshot(owner, label);
                await Reject(() => { LabelConfigCommandService.ValidateP804TrustedTableTargetSnapshot(owner, label); return Task.CompletedTask; });
            });
            await test(kind + " pinned historical STATISTIC version survives rename and deactivate without rebinding", async () => {
                var f = await Create(); var old = await f.Read(null); var oldPin = f.Label.Result.VersionId;
                await f.Label.Update(f.Inner.Ctx, f.Label.Payload with { Name = "Tên mới 🌿", IsActive = false });
                var owner = await f.Inner.Ctx.Labels.Find(l => l.Id == f.Label.Result.OwnerId).SingleAsync();
                Check(owner.VersionNo == 2 && owner.VersionId != oldPin, "writer did not create next version");
                Check(SnapshotId(await f.Read(null)) == SnapshotId(old), "pinned label silently rebound");
                Check((await f.Read(SnapshotId(old))).Contains("HISTORICAL"), "history lost after rename");
            });
            await test(kind + " native label current visibility revoked rejects config consumption", async () => {
                var f = await Create(); await f.Read(null);
                // Simulate ACL revocation; deliberately retain raw owner as evidence.
                await f.Inner.Ctx.Labels.UpdateOneAsync(l => l.Id == f.Label.Result.OwnerId,
                    Builders<LabelCatalogItem>.Update.Set(l => l.ScopeId, "cccccccccccccccccccccccc"));
                var raw = (await f.Inner.Ctx.Labels.Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Read(null), "CONFIG_DEPENDENCY_STALE");
                Check((await f.Inner.Ctx.Labels.Find(_ => true).SingleAsync()).ToJson() == raw, "label repaired");
            });
            foreach (var fault in new[] { "hash", "lineage", "deleted" })
                await test(kind + " native label rejects " + fault + " and retains raw", async () => {
                    var f = await Create(); await f.Read(null);
                    if (fault == "hash") await f.Inner.Ctx.Labels.UpdateOneAsync(_ => true, Builders<LabelCatalogItem>.Update.Set(l => l.ConfigHash, new string('0', 64)));
                    if (fault == "lineage") await f.Inner.Ctx.Labels.UpdateOneAsync(_ => true, Builders<LabelCatalogItem>.Update.Set(l => l.VersionNo, 5));
                    if (fault == "deleted") await f.Inner.Ctx.Labels.UpdateOneAsync(_ => true, Builders<LabelCatalogItem>.Update.Set(l => l.IsDeleted, true));
                    var raw = (await f.Inner.Ctx.Labels.Find(_ => true).SingleAsync()).ToJson();
                    await Reject(() => f.Read(null));
                    Check((await f.Inner.Ctx.Labels.Find(_ => true).SingleAsync()).ToJson() == raw, "damaged raw changed");
                });
        }
        await test("legacy TABLE_TARGET helper still accepts its real P8 label only", async () => {
            var ctx = Context(connection, database + "_legacy_label"); var me = Admin();
            var payload = new LabelState().Payload with { Usage = LabelUsages.TableTarget };
            var label = await new LabelConfigCommandService(ctx, me, new LabelEnumCatalogService(ctx, me), Runner(ctx))
                .CreateAsync(new(Guid.NewGuid().ToString("N"), 0, StatConfigCanonicalJson.EmptyConfigHash, payload), default);
            var owner = await ctx.Labels.Find(l => l.Id == label.OwnerId).SingleAsync();
            var snapshot = new DynamicFormStatisticLabelSnapshotDto(label.OwnerId, payload.Code!, payload.DataType!, payload.Usage!,
                payload.ScopeType!, payload.ScopeId, true, label.VersionNo, label.VersionId, label.ConfigHash);
            LabelConfigCommandService.ValidateP804TrustedTableTargetSnapshot(owner, snapshot);
            await Reject(() => { LabelConfigCommandService.ValidateTrustedStatisticSnapshot(owner, snapshot); return Task.CompletedTask; });
        });
        foreach (var fault in new[] { "usage", "datatype" })
            await test("real P8 native label rejects wrong " + fault + " without partial Form write", async () => {
                var ctx = Context(connection, database + "_wrong_label_" + fault); var label = new LabelState();
                label.Payload = fault == "usage" ? label.Payload with { Usage = LabelUsages.TableTarget }
                    : label.Payload with { DataType = LabelDataTypes.Boolean };
                await Reject(() => label.Configure(ctx));
                Check((await ctx.DynamicFormTemplates.Find(_ => true).SingleAsync()).ToJson() == Draft().ToJson(), "invalid label partially saved");
                Check(await ctx.Labels.CountDocumentsAsync(_ => true) == 1, "independent label was deleted");
            });
        await test("P8 v1 stays explicit; v2 upgrade retains original target and methods", async () => {
            var ctx = Context(connection, database + "_v1"); var owner = Draft();
            await ctx.DynamicFormTemplates.InsertOneAsync(owner);
            var tables = Tables(owner); var originalPlan = tables.Single(t => t.Id == "a").StatisticPlan!;
            var legacy = tables.Select(t => t.Id != "a" ? t : t with {
                StatisticPlan = t.StatisticPlan! with { Targets = t.StatisticPlan.Targets!.Where(p => p.Id != "a-legacy").ToArray() },
                StatisticTargets = t.StatisticTargets!.Select(s => s.Id != "a-legacy" ? s : s with {
                    Statistic = Fixtures.Json(new { aggregateOps = new[] { "sum", "avg" }, bucketMode = "none", showInDetail = true, showInTree = false }) }).ToList() }).ToArray();
            owner = await Patch(ctx, owner, legacy);
            var rawLegacy = StatConfigCanonicalJson.Canonicalize(Tables(owner).Single(t => t.Id == "a").StatisticTargets!);
            var published = Fixtures.LockedUnitInput(owner: Clone(owner)); var config = View(published.Template);
            Check(config.NativeTargets.Any(t => t.TargetId == "a-legacy"), "v1 dropped");
            foreach (var prefix in new[] { "BASIC_", "ADVANCED_" })
                await Reject(() => { DynamicFormNativeStatisticSelection.Select(config, [("a", "a-legacy", "a-legacy-0")], "refs", prefix,
                    (path, reason) => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { path, reason })); return Task.CompletedTask; }, "V1_EXPLICIT_UPGRADE_REQUIRED");
            await Reject(() => { NativeStatisticGenerationStage.Prepare(published.Template, "111111111111111111111111", "period", published.Generation,
                published.Sources, Fixtures.Limits); return Task.CompletedTask; }, "GENERATION_ACTIVE_V1_ADAPTER_REQUIRED");
            var upgrade = Tables(owner).Select(t => t.Id == "a" ? t with { StatisticPlan = originalPlan } : t).ToArray();
            owner = await Patch(ctx, owner, upgrade);
            Check(StatConfigCanonicalJson.Canonicalize(Tables(owner).Single(t => t.Id == "a").StatisticTargets!) == rawLegacy, "upgrade changed original source");
            var next = Fixtures.LockedUnitInput(owner: owner);
            Check(View(next.Template).NativeTargets.Count == 0, "overridden v1 still active");
            var result = NativeStatisticGenerationStage.Prepare(next.Template, "111111111111111111111111", "period", next.Generation, next.Sources, Fixtures.Limits with { ChunkBytes = 2048 });
            Check(result.Result.Groups.Any(g => g.Address.TargetId == "a-legacy"), "upgraded target dropped");
        });
        await test("P8 removing v2 override does not default missing v1 settings or overwrite owner", async () => {
            var ctx = Context(connection, database + "_v1_missing"); var owner = Draft(); await ctx.DynamicFormTemplates.InsertOneAsync(owner);
            var raw = owner.ToJson(); var tables = Tables(owner).Select(t => t.Id != "a" ? t : t with {
                StatisticPlan = t.StatisticPlan! with { Targets = t.StatisticPlan.Targets!.Where(p => p.Id != "a-legacy").ToArray() } }).ToArray();
            await Reject(() => Patch(ctx, owner, tables));
            Check((await ctx.DynamicFormTemplates.Find(_ => true).SingleAsync()).ToJson() == raw, "missing v1 repaired or partially written");
        });
    }

    private sealed record Fixture(PublicationCases.Fixture Inner, LabelState Label, Func<string?, Task<string>> Read, Func<Task<string>> Worker);
    internal sealed class LabelState
    {
        internal LabelConfigPayload Payload = new("metric.amount", "Số lượng 🌿", null, null, null, LabelUsages.Statistic,
            LabelDataTypes.Number, LabelValueSourceTypes.None, null, null, LabelScopeTypes.Unit, Unit, true);
        internal LabelConfigResult Result = null!;
        internal async Task<DynamicFormTemplate> Configure(MongoDbContext ctx)
        {
            var me = Admin(); var runner = Runner(ctx);
            Result = await new LabelConfigCommandService(ctx, me, new LabelEnumCatalogService(ctx, me), runner)
                .CreateAsync(new(Guid.NewGuid().ToString("N"), 0, StatConfigCanonicalJson.EmptyConfigHash, Payload), default);
            var owner = Draft(); await ctx.DynamicFormTemplates.InsertOneAsync(owner);
            var tables = Tables(owner).Select(t => t.Id != "a" ? t : t with { StatisticPlan = t.StatisticPlan! with {
                Targets = t.StatisticPlan.Targets!.Select(p => p.Id == "a-legacy" ? p with { StatisticLabelCodes = [Payload.Code!] } : p).ToArray() } }).ToArray();
            return await Patch(ctx, owner, tables);
        }
        internal async Task Update(MongoDbContext ctx, LabelConfigPayload payload)
        {
            var me = Admin(); Result = await new LabelConfigCommandService(ctx, me, new LabelEnumCatalogService(ctx, me), Runner(ctx))
                .UpdateAsync(Result.OwnerId, new(Guid.NewGuid().ToString("N"), Result.Revision, Result.ConfigHash, payload), default);
        }
    }
    private static MongoDbContext Context(string connection, string database) => new(Microsoft.Extensions.Options.Options.Create(new MongoOptions { ConnectionString = connection, Database = database }));
    private static StatConfigTransactionRunner Runner(MongoDbContext ctx) => new(ctx, NullLogger<StatConfigTransactionRunner>.Instance);
    private static MeAccessor Admin()
    {
        var http = new DefaultHttpContext(); http.Items[MeAccessor.MeItemKey] = new MeResponse("aaaaaaaaaaaaaaaaaaaaaaaa", "canvas-test-owner", "fixture", [], Unit, null, null, null, ["SYSTEM_ADMIN"], null, false);
        return new(new HttpContextAccessor { HttpContext = http });
    }
    private static DynamicFormTemplate Draft() => JsonSerializer.Deserialize<DynamicFormTemplate>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "p8-draft-owner.fixture.json")), StatConfigCanonicalJson.StrictJsonOptions)!;
    private static DynamicFormTemplate Clone(DynamicFormTemplate owner) => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<DynamicFormTemplate>(owner.ToBsonDocument());
    private static IReadOnlyList<DynamicFormNativeTableDto> Tables(DynamicFormTemplate owner) => DynamicFormNativeTableDefinition.ReadStored(owner.NativeTablesVersion, owner.TablesJson)!;
    private static DynamicFormStatisticConfigCommandService.NativeStatisticInputView View(DynamicFormTemplate owner) => DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(owner,
        owner.StatisticConfigId!, owner.StatisticConfigVersionId!, owner.StatisticConfigVersionNo, owner.StatisticConfigRevision, owner.StatisticConfigHash!);
    internal static async Task<DynamicFormTemplate> Patch(MongoDbContext ctx, DynamicFormTemplate owner, IReadOnlyList<DynamicFormNativeTableDto> tables)
    {
        await new DynamicFormStatisticConfigCommandService(ctx, Admin(), Runner(ctx)).PatchAsync(owner.Id, Fixtures.Json(new {
            commandId = Guid.NewGuid().ToString("N"), expectedRevision = owner.StatisticConfigRevision, expectedConfigHash = owner.StatisticConfigHash,
            payload = new { nativeStatistics = DynamicFormNativeStatisticState.Metadata(tables) } }), default);
        return await ctx.DynamicFormTemplates.Find(o => o.Id == owner.Id).SingleAsync();
    }
    private static string SnapshotId(string json) => JsonDocument.Parse(json).RootElement.GetProperty("SnapshotId").GetString()!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action, string? reason = null)
    { try { await action(); } catch (AppException ex) { if (reason is not null) Check(JsonSerializer.Serialize(ex.Details).Contains(reason), "Wrong rejection: " + JsonSerializer.Serialize(ex.Details)); return; } throw new Exception("Expected rejection " + reason); }
}
