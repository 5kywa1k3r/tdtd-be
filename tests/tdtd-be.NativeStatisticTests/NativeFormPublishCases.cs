using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.NativeStatisticTests;

internal static class NativeFormPublishCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, string evidence)
    {
        var index = 0;
        MongoDbContext Db() => new(Microsoft.Extensions.Options.Options.Create(new MongoOptions { ConnectionString = connection, Database = database + "_publish" + ++index }));
        foreach (var configured in new[] { false, true }) await test("startup backfill retains published native definition configured=" + configured, async () => {
            var db = Db(); var draft = await Create(db, configured);
            await Api(db).Form.PublishAsync(draft.Id, new(draft.Revision), default);
            var before = (await Load(db, draft.Id)).ToJson();
            await tdtd_be.Data.Indexes.DynamicFormVersionMetadataBackfill.RunAsync(db.DynamicFormTemplates, default);
            Check((await Load(db, draft.Id)).ToJson() == before, "startup rewrote valid published native data");
        });
        await test("startup rejects native structure drift without repairing raw", async () => {
            var db = Db(); var draft = await Create(db, false);
            await Api(db).Form.PublishAsync(draft.Id, new(draft.Revision), default);
            await db.DynamicFormTemplates.UpdateOneAsync(x => x.Id == draft.Id, Builders<DynamicFormTemplate>.Update.Set(x => x.TablesJson, "[]"));
            var before = (await Load(db, draft.Id)).ToJson();
            try { await tdtd_be.Data.Indexes.DynamicFormVersionMetadataBackfill.RunAsync(db.DynamicFormTemplates, default); throw new Exception("Drift accepted"); }
            catch (InvalidOperationException) { }
            Check((await Load(db, draft.Id)).ToJson() == before, "drift repaired");
        });
        await test("native-only create publish materializes empty config without inventing statistics", async () => {
            var db = Db(); var draft = await Create(db, false); var api = Api(db);
            var result = await api.Form.PublishAsync(draft.Id, new(draft.Revision), default);
            var owner = await Load(db, draft.Id);
            Check(result.IsPublished && !result.Actions.CanPublish && result.Actions.CanCreateVersion && result.Actions.CanUpdateStatistics, "wrong capabilities");
            Check(owner.StatisticConfigStatus == "LOCKED" && owner.StatisticConfigVersionNo == 1
                && owner.StatisticConfigSnapshots!.Count == 1 && owner.StatisticConfigSections!.NativeTargetSectionJson == "[]"
                && owner.StatisticConfigSections.NativePlanSectionJson is null, "empty configuration changed meaning");
            Check(owner.TablesJson == draft.TablesJson && owner.FieldsJson == "[]", "native-only schema lost");
            _ = View(owner);
        });
        await test("native v2 create P8 publish freezes complete schema and existing config identity", async () => {
            var db = Db(); var draft = await Create(db, true);
            var published = await Api(db).Form.PublishAsync(draft.Id, new(draft.Revision), default);
            var owner = await Load(db, draft.Id);
            Check(published.Schema!.Tables!.Count == 1 && owner.TablesJson == draft.TablesJson, "tables omitted");
            Check(owner.StatisticConfigHash == draft.StatisticConfigHash && owner.StatisticConfigVersionId == draft.StatisticConfigVersionId, "publish regenerated config");
            Check(owner.Revision == draft.Revision + 1 && owner.StatisticConfigSnapshots!.Count == draft.StatisticConfigSnapshots!.Count, "publish duplicated version");
            Check(View(owner).NativePlan!.Targets.Single().Configuration.Operations!.Count == 3, "methods lost");
            await File.WriteAllTextAsync(Path.Combine(evidence, "published-native-schema.json"), owner.PublishedSchemaSnapshotJson);
        });
        await test("native publish concurrent replay locks once", async () => {
            var db = Db(); var owner = await Create(db, true);
            var result = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Api(db).Form.PublishAsync(owner.Id, new(owner.Revision), default))));
            Check(result.All(x => x.PublishedSchemaHash == result[0].PublishedSchemaHash), "different snapshots");
            Check((await Load(db, owner.Id)).Revision == owner.Revision + 1, "duplicate publish writes");
        });
        await test("native stale publish revision cannot replace changed draft", async () => {
            var db = Db(); var owner = await Create(db, true);
            await db.DynamicFormTemplates.UpdateOneAsync(t => t.Id == owner.Id, Builders<DynamicFormTemplate>.Update.Inc(t => t.Revision, 1));
            var raw = (await Load(db, owner.Id)).ToJson();
            await Reject(() => Api(db).Form.PublishAsync(owner.Id, new(owner.Revision), default));
            Check((await Load(db, owner.Id)).ToJson() == raw, "stale publish wrote");
        });
        foreach (var fault in new[] { "hash", "missing-statistic", "empty" })
            await test("native publish rejects " + fault + " and retains raw", async () => {
                var db = Db(); var owner = await Create(db, true);
                if (fault == "hash") owner.StatisticConfigHash = new string('b', 64);
                if (fault == "empty") { owner.TablesJson = "[]"; owner.FieldsJson = "[]"; }
                if (fault == "missing-statistic") {
                    var tables = Tables(owner); var plan = tables[0].StatisticPlan!;
                    tables[0] = tables[0] with { StatisticPlan = plan with { Targets = [plan.Targets![0] with { Operations = null }] } };
                    owner.TablesJson = Canonical(tables);
                }
                await db.DynamicFormTemplates.ReplaceOneAsync(t => t.Id == owner.Id, owner);
                var raw = (await Load(db, owner.Id)).ToJson(); await Reject(() => Api(db).Form.PublishAsync(owner.Id, new(owner.Revision), default));
                Check((await Load(db, owner.Id)).ToJson() == raw, "raw repaired or partially published");
            });
        await test("native publish transaction interruption rolls back schema and config lock", async () => {
            var db = Db(); var owner = await Create(db, true); var raw = owner.ToJson();
            try { await Api(db, abort: true).Form.PublishAsync(owner.Id, new(owner.Revision), default); throw new Exception("Missing interruption"); }
            catch (Interrupted) { }
            Check((await Load(db, owner.Id)).ToJson() == raw, "partial publication survived rollback");
        });
        await test("native publish preserves two independent tables choice codes and rule order", async () => {
            var db = Db(); var a = Fixtures.Table("singleSelect");
            var b = a with { Id = "table2", Order = 1,
                Fields = a.Fields!.Select(f => f with { Id = "second-" + f.Id }).ToList(),
                Rows = a.Rows!.Select(r => r with { Id = "second-" + r.Id }).ToList(),
                TypeConfig = a.TypeConfig! with { Rules = a.TypeConfig.Rules!.Select(r => r with {
                    Target = r.Target! with { FieldId = "second-" + r.Target.FieldId } }).ToList() } };
            var schema = DynamicFormSchemaAdapter.FromLegacy(Fixtures.Sections, "[]", null, "[]", 1, Canonical(new[] { a, b }));
            var api = Api(db);
            var draft = await api.Form.CreateAsync(new(null, "Hai bảng", null, [], 1, null, null, null, null, Schema: schema), default);
            var before = await Load(db, draft.Id);
            await api.Form.PublishAsync(draft.Id, new(draft.Revision), default);
            var after = await Load(db, draft.Id);
            Check(before.TablesJson == after.TablesJson && Tables(after).Count == 2, "table identity or metadata changed");
            using var snapshot = JsonDocument.Parse(after.PublishedSchemaSnapshotJson!);
            Check(Canonical(snapshot.RootElement.GetProperty("tables")) == Canonical(Tables(before)), "snapshot lost options/rules");
        });
        await test("native publish refuses active v1 without upgrading or repairing raw", async () => {
            var db = Db(); var table = Fixtures.Table() with { StatisticTargets = [new() {
                Id = "legacy", Target = new() { Scope = "column", FieldId = "a" }, IsStatistic = true,
                Statistic = Fixtures.Json(new { aggregateOps = new[] { "sum", "avg" }, bucketMode = "none", showInDetail = true, showInTree = false }),
                StatisticLabelCodes = [] }] };
            var initial = table with { StatisticTargets = table.StatisticTargets!.Select(t => t with {
                IsStatistic = false, Statistic = default, StatisticLabelCodes = null }).ToList() };
            var schema = DynamicFormSchemaAdapter.FromLegacy(Fixtures.Sections, "[]", null, "[]", 1, Canonical(new[] { initial }));
            var api = Api(db);
            var draft = await api.Form.CreateAsync(new(null, "V1 chưa nâng cấp", null, [], 1, null, null, null, null, Schema: schema), default);
            await Patch(db, draft.Id, [table]);
            var owner = await Load(db, draft.Id); var raw = owner.ToJson();
            await Reject(() => Api(db).Form.PublishAsync(draft.Id, new(owner.Revision), default));
            Check((await Load(db, draft.Id)).ToJson() == raw, "v1 implicitly upgraded");
        });
        await test("native publish rejects changed label pins without partial lock", async () => {
            var db = Db(); var label = new NativeLabelCases.LabelState(); var owner = await label.Configure(db);
            await label.Update(db, label.Payload with { Name = "Tên sau khi cấu hình" });
            var raw = (await Load(db, owner.Id)).ToJson();
            await Reject(() => Api(db).Form.PublishAsync(owner.Id, new(owner.Revision), default));
            Check((await Load(db, owner.Id)).ToJson() == raw, "stale label pins published or rebound");
        });
        await test("native real publish reaches Direct job and P8 change retains old history and concurrency fence", async () => {
            var f = await PublicationCases.Fixture.Create(connection, database + "_real_direct", async ctx => {
                var draft = await Create(ctx, true); await Api(ctx).Form.PublishAsync(draft.Id, new(draft.Revision), default); return await Load(ctx, draft.Id);
            });
            await f.Project(); var job = await f.Job(); var old = await f.Reader().ReadNativeAsync(f.Request(), default);
            var artifact = await NativeStatisticPublicationContract.ReadAsync(f.Ctx.Db, job, default);
            var before = await Load(f.Ctx, f.Template.Id); var tables = Tables(before);
            var plan = tables[0].StatisticPlan!;
            tables[0] = tables[0] with { StatisticPlan = plan with { Targets = plan.Targets!.Select(t => t with {
                Operations = t.Operations!.Select(op => op.Method == "CONCAT" ? op with { Options = Fixtures.Json(Fixtures.ConcatOptions("\r\n--\r\n")) } : op).ToArray()
            }).ToArray() } };
            await Patch(f.Ctx, before.Id, tables);
            var changed = await Load(f.Ctx, before.Id);
            Check(changed.NativeStatisticPublicationFence == before.NativeStatisticPublicationFence && changed.NativeStatisticPublicationFence > 0, "P8 dropped job fence");
            Check(changed.PublishedSchemaHash == before.PublishedSchemaHash && changed.PublishedSchemaSnapshotJson == before.PublishedSchemaSnapshotJson
                && changed.StatisticConfigHash != before.StatisticConfigHash, "schema/config pins conflated");
            _ = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(changed);
            var history = await f.Reader().ReadNativeAsync(f.Request() with { Historical = true, RunId = job.Id, GenerationId = job.GenerationId }, default);
            Check(Canonical(history.Result) == Canonical(old.Result) && Canonical(history.NativeMetadata) == Canonical(old.NativeMetadata), "history rebound to current metadata");
            Check((await f.Reader().ReadNativeAsync(f.Request(), default)).Metadata.State == "STALE", "old result falsely current");
            var sources = await Task.WhenAll(f.Reports.Select(async report => (report, await f.Payload.LoadReportPayloadAsync(report))));
            var generation = artifact.Generation with { ConfigId = changed.StatisticConfigId!, ConfigVersionId = changed.StatisticConfigVersionId!,
                ConfigVersionNo = changed.StatisticConfigVersionNo, ConfigRevision = changed.StatisticConfigRevision, ConfigHash = changed.StatisticConfigHash! };
            var next = NativeStatisticGenerationStage.Prepare(changed, f.Work, f.Reports[0].PeriodInstanceKey!, generation, sources, artifact.Limits);
            var wire = JsonSerializer.Deserialize<NativeStatisticResultDocument>(Canonical(next.Result), StatConfigCanonicalJson.StrictJsonOptions)!;
            var projection = NativeStatisticResultProjection.Project(next.DefinitionJson, next.ConfigurationJson, null, wire);
            Check(projection.Result.Groups.SelectMany(g => g.Operations).Single(o => o.Method == "CONCAT").TextChunks!.Any(c => c.Text.Contains("--")), "new separator ignored");
            await File.WriteAllTextAsync(Path.Combine(evidence, "published-config-version-result.json"), Canonical(new { native = projection.Result, nativeMetadata = projection.Metadata }));
        });
        await test("native explicit removal of plan yields empty result and retains published schema", async () => {
            var f = await PublicationCases.Fixture.Create(connection, database + "_disable", async ctx => {
                var draft = await Create(ctx, true); await Api(ctx).Form.PublishAsync(draft.Id, new(draft.Revision), default); return await Load(ctx, draft.Id);
            });
            var original = f.Template.PublishedSchemaHash;
            await Patch(f.Ctx, f.Template.Id, Tables(f.Template).Select(t => t with { StatisticPlan = null }).ToList());
            f.Template = await Load(f.Ctx, f.Template.Id);
            await f.Project(); var result = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(result.Result!.Groups.Count == 0 && result.NativeMetadata!.Targets.Count == 0 && f.Template.PublishedSchemaHash == original, "disabled config restored old snapshot methods");
        });
        await test("native real publish next-version keeps axes and old owner immutable then repins config", async () => {
            var db = Db(); var draft = await Create(db, true); var api = Api(db);
            var published = await api.Form.PublishAsync(draft.Id, new(draft.Revision), default);
            var before = await Load(db, draft.Id);
            var successor = await api.Form.CreateNextVersionAsync(before.Id, new(before.Revision), default);
            var next = await Load(db, successor.Id);
            Check(next.TablesJson == before.TablesJson && next.FamilyId == before.FamilyId && next.PreviousVersionId == before.Id && !next.IsPublished, "version lost identity/axes");
            var old = await Load(db, before.Id);
            Check(old.PublishedSchemaSnapshotJson == before.PublishedSchemaSnapshotJson && old.StatisticConfigHash == before.StatisticConfigHash, "old version rewritten");
            var nextPublished = await api.Form.PublishAsync(next.Id, new(next.Revision), default);
            var locked = await Load(db, next.Id);
            Check(nextPublished.IsPublished && locked.StatisticConfigId != before.StatisticConfigId && View(locked).NativePlan!.Targets.Count == 1, "successor reused old config owner");
        });
        foreach (var fault in new[] { "axis", "type", "target" })
            await test("native published immutable structure rejects " + fault, async () => {
                var db = Db(); var draft = await Create(db, true); await Api(db).Form.PublishAsync(draft.Id, new(draft.Revision), default);
                var owner = await Load(db, draft.Id); var tables = Tables(owner);
                if (fault == "axis") tables[0] = tables[0] with { Fields = tables[0].Fields!.AsEnumerable().Reverse().ToList() };
                if (fault == "type") tables[0] = tables[0] with { TypeConfig = tables[0].TypeConfig! with { Sequence = 9 } };
                if (fault == "target") tables[0] = tables[0] with { StatisticTargets = [new() { Id = "unpublished", Target = new() { Scope = "column", FieldId = "a" }, IsStatistic = false }] };
                owner.TablesJson = Canonical(tables);
                try { _ = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(owner); throw new Exception("Structure accepted"); }
                catch (InvalidOperationException) { }
            });
    }

    private static MeAccessor Me()
    {
        var http = new DefaultHttpContext(); http.Items[MeAccessor.MeItemKey] = new MeResponse("aaaaaaaaaaaaaaaaaaaaaaaa", "canvas-test", "Canvas", [],
            "bbbbbbbbbbbbbbbbbbbbbbbb", null, null, null, ["SYSTEM_ADMIN"], null, false);
        return new(new HttpContextAccessor { HttpContext = http });
    }
    private static (DynamicFormService Form, DynamicFormStatisticConfigCommandService Config) Api(MongoDbContext db, bool abort = false)
    {
        var me = Me(); var runner = new StatConfigTransactionRunner(db, NullLogger<StatConfigTransactionRunner>.Instance);
        var config = new DynamicFormStatisticConfigCommandService(db, me, abort ? new Abort(runner) : runner);
        return (new(db, me, config, new LabelEnumCatalogService(db, me)), config);
    }
    internal static async Task<DynamicFormTemplate> Create(MongoDbContext db, bool statistics)
    {
        var table = Fixtures.Table();
        var schema = DynamicFormSchemaAdapter.FromLegacy(Fixtures.Sections, "[]", null, "[]", 1, Canonical(new[] { table }));
        var detail = await Api(db).Form.CreateAsync(new(null, "Hồ sơ Canvas publish", null, [], 1, null, null, null, null, Schema: schema), default);
        if (statistics)
        {
            var target = DynamicFormNativeStatisticPlan.Read(Fixtures.Plan(["SUM", "AVG", "CONCAT"])).Targets![0];
            var state = new DynamicFormNativeTableStatisticStateDto { Id = target.TargetId, IsStatistic = true, Selector = target.Selector,
                Grouping = target.Grouping, Input = target.Input, Order = target.Order, Operations = target.Operations,
                StatisticLabelCodes = target.StatisticLabelCodes, ShowInDetail = target.ShowInDetail, ShowInTree = target.ShowInTree };
            await Patch(db, detail.Id, [table with { StatisticPlan = new(2, [state]) }]);
        }
        return await Load(db, detail.Id);
    }
    private static async Task Patch(MongoDbContext db, string id, IReadOnlyList<DynamicFormNativeTableDto> tables)
    {
        var api = Api(db); var current = await api.Config.GetAsync(id, default);
        await api.Config.PatchAsync(id, Fixtures.Json(new { commandId = Guid.NewGuid().ToString("N"), expectedRevision = current.Revision,
            expectedConfigHash = current.ConfigHash, payload = new { nativeStatistics = DynamicFormNativeStatisticState.Metadata(tables) } }), default);
    }
    private static Task<DynamicFormTemplate> Load(MongoDbContext db, string id) => db.DynamicFormTemplates.Find(t => t.Id == id).SingleAsync();
    private static List<DynamicFormNativeTableDto> Tables(DynamicFormTemplate owner) => DynamicFormNativeTableDefinition.ReadStored(owner.NativeTablesVersion, owner.TablesJson)!;
    private static DynamicFormStatisticConfigCommandService.NativeStatisticInputView View(DynamicFormTemplate o)
        => DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(o, o.StatisticConfigId!, o.StatisticConfigVersionId!, o.StatisticConfigVersionNo, o.StatisticConfigRevision, o.StatisticConfigHash!);
    private static string Canonical(object? value) => StatConfigCanonicalJson.Canonicalize(value);
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is AppException or InvalidOperationException or JsonException) { return; } throw new Exception("Expected rejection"); }
    private sealed class Interrupted : Exception { }
    private sealed class Abort(IStatConfigTransactionRunner inner) : IStatConfigTransactionRunner
    {
        public Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct = default)
            => inner.ExecuteAsync<T>(async (session, token) => { _ = await operation(session, token); throw new Interrupted(); }, ct);
    }
}
