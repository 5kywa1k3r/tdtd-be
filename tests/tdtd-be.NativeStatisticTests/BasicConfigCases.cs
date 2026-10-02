using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class BasicConfigCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var next = 0;
        Task<Fixture> Create() => Fixture.Create(connection, database + "_basic" + ++next);
        await test("Basic legacy canonical payload/hash unchanged when native references omitted", () =>
        {
            var payload = Payload(null);
            var expected = StatConfigCanonicalJson.Canonicalize(new { payload.SourceScope, payload.PeriodRule,
                payload.GroupingHints, payload.DetailHints, payload.Targets });
            Check(StatConfigCanonicalJson.Canonicalize(WorkAssignmentBasicSummaryService.NormalizeP804Payload(payload)) == expected,
                "legacy canonical payload changed");
            return Task.CompletedTask;
        });
        foreach (var invalid in new[] { "missing", "duplicate", "control", "limit", "null", "unknown-property" })
            await test("Basic native reference schema rejects " + invalid, async () =>
            {
                var f = await Create();
                var refs = f.References.ToArray();
                refs = invalid switch {
                    "missing" => [refs[0] with { OperationId = null }],
                    "duplicate" => [refs[0], refs[0]],
                    "control" => [refs[0] with { TableId = "A\nB" }],
                    "limit" => Enumerable.Repeat(refs[0], 1001).ToArray(),
                    "null" => [null!], _ => refs };
                var state = await f.Read();
                var body = Command(state, Payload(refs));
                if (invalid == "unknown-property")
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!;
                    node["payload"]!["nativeTargets"]![0]!["method"] = "SUM";
                    body = JsonSerializer.SerializeToElement(node);
                }
                await Reject(() => f.Service().PutP8ConfigAsync(f.Assignment, f.Template.Id, body, default));
                Check(await f.Ctx.WorkAssignmentBasicSummaryConfigs.CountDocumentsAsync(_ => true) == 0, "invalid config persisted");
                Check(await f.Ctx.StatConfigCommandReceipts.CountDocumentsAsync(_ => true) == 0, "invalid receipt persisted");
            });
        foreach (var invalid in new[] { "table", "target", "operation", "case" })
            await test("Basic native reference exact resolution rejects " + invalid, async () =>
            {
                var f = await Create(); var item = f.References[0];
                item = invalid switch { "table" => item with { TableId = "missing" }, "target" => item with { TargetId = "missing" },
                    "operation" => item with { OperationId = "missing" }, _ => item with { TableId = item.TableId!.ToUpperInvariant() } };
                Check(item != f.References[0], "fixture must change the referenced identity");
                await Reject(() => f.Put([item]));
                Check(await f.Ctx.WorkAssignmentBasicSummaryConfigs.CountDocumentsAsync(_ => true) == 0, "invalid reference persisted");
            });
        await test("Basic native plan preserves selected options labels selector and ordering", async () =>
        {
            var f = await Create(); var refs = f.References.Reverse().ToArray();
            var plan = WorkAssignmentBasicSummaryService.ResolveNativeBasicPlan(f.Template, refs);
            var original = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(f.Template,
                f.Template.StatisticConfigId!, f.Template.StatisticConfigVersionId!, f.Template.StatisticConfigVersionNo,
                f.Template.StatisticConfigRevision, f.Template.StatisticConfigHash!).NativePlan!;
            foreach (var group in refs.GroupBy(r => (r.TableId, r.TargetId)))
            {
                var target = original.Targets.Single(t => (t.Configuration.TableId, t.Configuration.TargetId) == group.Key).Configuration;
                var expected = target with { Operations = group.Select(r => target.Operations!.Single(op => op.OperationId == r.OperationId)).ToArray() };
                Check(StatConfigCanonicalJson.Canonicalize(plan.Targets!.Single(t => (t.TableId, t.TargetId) == group.Key)) ==
                    StatConfigCanonicalJson.Canonicalize(expected), "selected target changed semantic settings");
            }
        });
        await test("Basic selected AVG plan calculates exact sum/count from raw reports", () =>
        {
            var input = Fixtures.LockedUnitInput();
            var plan = WorkAssignmentBasicSummaryService.ResolveNativeBasicPlan(input.Template,
                [new("a", "a-legacy", "a-legacy-1")]);
            Check(plan.Targets!.Single().Operations!.Single().Method == "AVG", "reference selected wrong method");
            var sources = input.Sources.Select(s => NativeStatisticCalculationSource.Capture(s.Report, s.Payload)).ToArray();
            var pins = sources.Select(s => s.Pin).ToArray();
            var result = NativeTableStatisticCalculator.Calculate(Canonical(plan),
                DynamicFormNativeTableDefinition.ReadStored(input.Template.NativeTablesVersion, input.Template.TablesJson)!,
                input.Template.SectionsJson, input.Template.FieldsJson, input.Template.BlocksJson,
                input.Template.PublishedSchemaHash!, pins, WorkReportNativeSourcePin.Digest(pins), sources, Fixtures.Limits);
            var numeric = result.Groups.Single(g => g.Address.RowId == "a-r1").Operations.Single().Numeric!;
            Check(numeric.Sum == "8" && numeric.Count == 2, "AVG lost raw numeric observations");
            Check(result.Groups.All(g => g.Operations.Count == 1 && g.Operations[0].Method == "AVG"), "unselected operations leaked");
            return Task.CompletedTask;
        });
        await test("Basic native config real put lock replay history and next draft", async () =>
        {
            var f = await Create(); var before = await f.Read();
            var body = Command(before, Payload(f.References));
            var saved = await f.Service().PutP8ConfigAsync(f.Assignment, f.Template.Id, body, default);
            var replay = await f.Service().PutP8ConfigAsync(f.Assignment, f.Template.Id, body, default);
            Check(saved.Identity == replay.Identity || Canonical(saved) == Canonical(replay), "receipt replay changed");
            Check(saved.Identity.DependencyPins.Any(p => p.StartsWith("NATIVE_BASIC_PLAN:", StringComparison.Ordinal)), "native dependency missing");
            var locked = await f.Service().LockP8ConfigAsync(f.Assignment, f.Template.Id, Command(saved, new { }), default);
            Check(locked.Identity.Status == "LOCKED", "lock failed");
            Check(Canonical((await f.Read()).Payload) == Canonical(saved.Payload), "readback dropped native references");
            var draft = await f.Service().CreateNextP8DraftAsync(f.Assignment, f.Template.Id, Command(locked, new { }), default);
            Check(draft.Identity.VersionNo == 2 && draft.PreviousVersionId == locked.Identity.VersionId, "next version lineage lost");
            var historical = await f.Service().GetP8ConfigVersionAsync(f.Assignment, f.Template.Id, 1, default);
            Check(historical.Identity.ConfigHash == locked.Identity.ConfigHash && Canonical(historical.Payload) == Canonical(saved.Payload), "history changed");
            await Reject(() => f.Service().LockP8ConfigAsync(f.Assignment, f.Template.Id, Command(saved, new { }), default));
            Check(Canonical((await f.Read()).Payload) == Canonical(draft.Payload), "stale CAS overwrote draft");
        });
        await test("Basic native config readback survives actual database restart", async () =>
        {
            var f = await Create(); var saved = await f.Put(f.References);
            await restart();
            Check(Canonical((await f.Read()).Payload) == Canonical(saved.Payload), "new service lost config");
        });
        await test("Basic native lock rejects changed Form snapshot and keeps draft raw", async () =>
        {
            var f = await Create(); var saved = await f.Put(f.References);
            var raw = await f.Ctx.WorkAssignmentBasicSummaryConfigs.Find(_ => true).SingleAsync();
            await f.Ctx.DynamicFormTemplates.UpdateOneAsync(x => x.Id == f.Template.Id,
                Builders<DynamicFormTemplate>.Update.Set(x => x.PublishedSchemaHash, new string('0', 64)));
            await Reject(() => f.Service().LockP8ConfigAsync(f.Assignment, f.Template.Id, Command(saved, new { }), default));
            Check((await f.Ctx.WorkAssignmentBasicSummaryConfigs.Find(_ => true).SingleAsync()).ToJson() == raw.ToJson(), "drift overwrote config");
        });
        await test("Basic native legacy result caller rejects before snapshot or job writes", async () =>
        {
            var f = await Create(); var saved = await f.Put(f.References);
            await f.Service().LockP8ConfigAsync(f.Assignment, f.Template.Id, Command(saved, new { }), default);
            await Reject(() => f.Service().GetSummaryAsync(new() { ScopeAssignmentId = f.Assignment }, f.Actor, default),
                "BASIC_SUMMARY_NATIVE_RESULT_CONSUMER_REQUIRED");
            Check(await f.Ctx.WorkAssignmentBasicSummarySnapshots.CountDocumentsAsync(_ => true) == 0, "scalar snapshot created");
        });
        await test("Basic native unauthorized write rejected before payload validation", async () =>
        {
            var f = await Create();
            await Reject(() => f.Service("cccccccccccccccccccccccc").PutP8ConfigAsync(f.Assignment, f.Template.Id, Fixtures.Json(new { }), default),
                "BASIC_SUMMARY_CONFIG_MANAGE_FORBIDDEN");
            Check(await f.Ctx.StatConfigCommandReceipts.CountDocumentsAsync(_ => true) == 0, "unauthorized receipt");
        });
    }

    private static string Canonical(object value) => StatConfigCanonicalJson.Canonicalize(value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> operation, string? reason = null)
    {
        try { await operation(); }
        catch (AppException ex)
        {
            if (reason is not null) Check(JsonSerializer.Serialize(ex.Details).Contains(reason, StringComparison.Ordinal), "wrong rejection: " + JsonSerializer.Serialize(ex.Details));
            return;
        }
        throw new Exception("Expected explicit config rejection");
    }
    private static WorkAssignmentBasicSummaryConfigPayload Payload(IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload>? refs)
        => new(new("SELF", null, null, null, null), new("ALL_PERIODS", null, null, null), [], new(false, 12000), [], refs);
    private static JsonElement Command(WorkAssignmentBasicSummaryConfigReadback state, object payload)
        => Fixtures.Json(new { commandId = Guid.NewGuid().ToString("N"), expectedRevision = state.Identity.Revision,
            expectedConfigHash = state.Identity.ConfigHash, payload });

    private sealed class Fixture
    {
        internal required MongoDbContext Ctx;
        internal required DynamicFormTemplate Template;
        internal required IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload> References;
        internal const string ActorId = "aaaaaaaaaaaaaaaaaaaaaaaa";
        internal string Actor => ActorId;
        internal string Assignment = "bbbbbbbbbbbbbbbbbbbbbbbb";
        internal static async Task<Fixture> Create(string connection, string database)
        {
            var template = Fixtures.LockedUnitInput().Template;
            var view = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(template,
                template.StatisticConfigId!, template.StatisticConfigVersionId!, template.StatisticConfigVersionNo,
                template.StatisticConfigRevision, template.StatisticConfigHash!);
            var ctx = new MongoDbContext(Microsoft.Extensions.Options.Options.Create(new MongoOptions { ConnectionString = connection, Database = database }));
            var f = new Fixture { Ctx = ctx, Template = template, References = view.NativePlan!.Targets
                .SelectMany(t => t.Configuration.Operations!.Select(op => new WorkAssignmentBasicSummaryNativeTargetPayload(
                    t.Configuration.TableId, t.Configuration.TargetId, op.OperationId))).ToArray() };
            Check(f.References.Count > 0, "fixture needs native operations");
            await ctx.DynamicFormTemplates.InsertOneAsync(template);
            await ctx.WorkAssignments.InsertOneAsync(new() { Id = f.Assignment, WorkId = "111111111111111111111111",
                CreatedByUserId = ActorId, DynamicFormTemplateId = template.Id, IsActive = true });
            return f;
        }
        internal WorkAssignmentBasicSummaryService Service(string? actor = null)
        {
            var http = new DefaultHttpContext();
            http.Items[MeAccessor.MeItemKey] = new MeResponse(actor ?? Actor, "fixture", "fixture", [], "dddddddddddddddddddddddd", null, null, null, [], null, false);
            // These dependencies must not be called by a configuration mutation
            // or by the guarded legacy result path.
            return new(Ctx, null!, null!, null!, null!, new(new HttpContextAccessor { HttpContext = http }),
                new StatConfigTransactionRunner(Ctx, NullLogger<StatConfigTransactionRunner>.Instance), new Activation());
        }
        internal Task<WorkAssignmentBasicSummaryConfigReadback> Read() => Service().GetP8ConfigAsync(Assignment, Template.Id, default);
        internal async Task<WorkAssignmentBasicSummaryConfigReadback> Put(IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload> refs)
            => await Service().PutP8ConfigAsync(Assignment, Template.Id, Command(await Read(), Payload(refs)), default);
    }
    private sealed class Activation : IStatRunCandidateActivation
    {
        private readonly StatRunCandidateBinding binding = new("canvas-isolated", "canvas-basic-native-config", 10, "test-catalog",
            Fixtures.Schema, Fixtures.Schema, Fixtures.Schema, Fixtures.Schema, Fixtures.Schema, "canvas_test", []);
        public StatRunCandidateBinding RequireFoundation(string capabilityId, string routeId) => binding;
        public StatRunCandidateEvaluation EvaluateFoundation(string capabilityId, string routeId) => new(true, null, routeId, capabilityId, binding);
        public StatRunCandidateBinding RequireCapability(string capabilityId, string routeId) => binding;
        public StatRunCandidateEvaluation EvaluateCapability(string capabilityId, string routeId) => new(true, null, routeId, capabilityId, binding);
    }
}
