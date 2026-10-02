using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.SummaryTokens;

namespace tdtd_be.NativeStatisticTests;

internal static class AdvancedConfigCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart)
    {
        var index = 0;
        async Task<Fixture> Create()
        {
            var inner = await PublicationCases.Fixture.Create(connection, database + "_advancedconfig" + ++index);
            await inner.Ctx.Units.InsertOneAsync(new Unit { Id = "bbbbbbbbbbbbbbbbbbbbbbbb", FullName = "Isolated Canvas unit", Code = "01" });
            return new(inner);
        }
        await test("Advanced omitted and null native refs preserve legacy canonical payload and hash", () =>
        {
            var payload = Payload(null);
            var expected = Canonical(new { payload.SourceScope,
                Sections = payload.Sections!.Select(s => new { s.SectionId, s.IsCumulative, s.Targets }).ToArray(),
                payload.HierarchyGrains, payload.Grouping, payload.Ordering, payload.Description });
            var normalized = WorkAssignmentAdvancedSummaryConfigService.NormalizeP805Payload(payload, "part-main");
            Check(Canonical(normalized) == expected, "legacy canonical JSON changed");
            using var doc = JsonDocument.Parse(expected);
            Check(Canonical(StatConfigCanonicalJson.DeserializeStrict<WorkAssignmentAdvancedSummaryConfigPayload>(doc.RootElement)) == expected,
                "omitted field was defaulted");
            Check(StatConfigCanonicalJson.HashObject(new { payload = normalized, dependencyPins = new[] { "old-pin" } }) ==
                StatConfigCanonicalJson.HashObject(new { payload = doc.RootElement, dependencyPins = new[] { "old-pin" } }), "legacy config hash changed");
            return Task.CompletedTask;
        });
        foreach (var fault in new[] { "missing", "duplicate", "control", "null", "limit", "unknown-property", "combined-limit" })
            await test("Advanced native schema rejects " + fault + " without writes", async () =>
            {
                var f = await Create(); var refs = f.References;
                refs = fault switch { "missing" => [refs[0] with { OperationId = null }], "duplicate" => [refs[0], refs[0]],
                    "control" => [refs[0] with { TableId = "a\nb" }], "null" => [null!],
                    "limit" => Enumerable.Repeat(refs[0], 1001).ToArray(), _ => refs };
                var payload = Payload(refs);
                if (fault == "combined-limit") payload = payload with { Sections = [new("part-main", true,
                    Enumerable.Range(0, 249).Select(i => new WorkAssignmentAdvancedSummaryTargetPayload("field-" + i, "NUMBER", "SUM")).ToArray(), [refs[0]])] };
                var body = Command(await f.Read(), payload);
                if (fault == "unknown-property")
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!;
                    node["payload"]!["sections"]![0]!["nativeTargets"]![0]!["method"] = "AVG";
                    body = JsonSerializer.SerializeToElement(node);
                }
                await Reject(() => f.Put(body));
                Check(await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.CountDocumentsAsync(_ => true) == 0
                    && await f.Inner.Ctx.StatConfigCommandReceipts.CountDocumentsAsync(_ => true) == 0, "invalid input wrote config/receipt");
            });
        foreach (var fault in new[] { "other-section", "target", "operation", "case" })
            await test("Advanced native exact reference rejects " + fault, async () =>
            {
                var f = await Create(); var reference = f.References[0];
                reference = fault switch { "other-section" => new("records", "records-text-plan", "anything"),
                    "target" => reference with { TargetId = "missing" }, "operation" => reference with { OperationId = "missing" },
                    _ => reference with { TableId = reference.TableId!.ToUpperInvariant() } };
                await Reject(async () => await f.Put(Command(await f.Read(), Payload([reference]))), fault == "other-section" || fault == "case"
                    ? "ADVANCED_NATIVE_TABLE_OUTSIDE_SECTION" : fault == "target" ? "ADVANCED_NATIVE_TARGET_NOT_FOUND" : "ADVANCED_NATIVE_OPERATION_NOT_FOUND");
                Check(await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.CountDocumentsAsync(_ => true) == 0, "bad reference persisted");
            });
        await test("Advanced selected plan preserves methods options separators order and target metadata", async () =>
        {
            var f = await Create(); var refs = f.References.Reverse().ToArray();
            var selected = WorkAssignmentAdvancedSummaryConfigService.ResolveNativeAdvancedPlan(f.Inner.Template, "part-main", refs);
            var view = View(f.Inner.Template);
            foreach (var group in refs.GroupBy(r => (r.TableId, r.TargetId)))
            {
                var original = view.NativePlan!.Targets.Single(t => (t.Configuration.TableId, t.Configuration.TargetId) == group.Key).Configuration;
                var expected = original with { Operations = group.Select(r => original.Operations!.Single(o => o.OperationId == r.OperationId)).ToArray() };
                Check(Canonical(selected.Targets!.Single(t => (t.TableId, t.TargetId) == group.Key)) == Canonical(expected), "selected metadata changed");
            }
            var basic = WorkAssignmentBasicSummaryService.ResolveNativeBasicPlan(f.Inner.Template,
                refs.Select(r => new tdtd_be.DTOs.WorkAssignments.BasicSummary.WorkAssignmentBasicSummaryNativeTargetPayload(r.TableId, r.TargetId, r.OperationId)).ToArray());
            Check(Canonical(selected) == Canonical(basic), "Basic and Advanced selected different semantics");
        });
        await test("Advanced native P805 put lock replay and validation receipt preserve complete config", async () =>
        {
            var f = await Create(); var state = await f.Read(); var payload = Payload(f.References);
            var body = Command(state, payload); var saved = await f.Put(body); var replay = await f.Put(body);
            Check(Canonical(saved.Identity) == Canonical(replay.Identity) && Canonical(saved.Payload) == Canonical(payload), "put/replay changed payload");
            Check(saved.Identity.DependencyPins.Any(p => p.StartsWith("NATIVE_ADVANCED_PLAN:", StringComparison.Ordinal)), "native dependency missing");
            var lockBody = Command(saved, new { });
            var locked = await f.Service().LockP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", lockBody, default);
            var lockReplay = await f.Service().LockP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", lockBody, default);
            Check(Canonical(lockReplay) == Canonical(locked), "lock replay changed receipt");
            var ledgers = await f.Inner.Ctx.WorkSummaryTokenLedgers.Find(_ => true).ToListAsync();
            Check(ledgers.Count == 2 && ledgers.Single(l => l.RecordKind == WorkSummaryTokenLedgerRecordKinds.Pool).UsedUnits == 0,
                "free lock/replay created duplicate token debit");
            var receipt = locked.ValidationReceipt ?? throw new Exception("Missing validation receipt");
            Check(locked.Identity.Status == "LOCKED" && receipt.TargetCount == f.References.Length + 1,
                "lock receipt omitted native targets");
            Check(Canonical(locked.Payload) == Canonical(payload) && !receipt.HierarchyWrite && !receipt.PreviewWrite,
                "lock changed contract or enabled runtime");
            var row = await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.Find(_ => true).SingleAsync();
            Check(row.ConfigJson == Canonical(payload) && row.PreviewResultJson is null, "config generated preview data");
            Check(await f.Inner.Ctx.WorkAssignmentAdvancedSummaryDayNodes.CountDocumentsAsync(_ => true) == 0, "config touched hierarchy");
        });
        await test("Advanced two sections retain independent Table operation references and identities", async () =>
        {
            var f = await Create(); var main = await f.Save();
            var refs = View(f.Inner.Template).NativePlan!.Targets.Where(t => t.Configuration.TableId == "records")
                .SelectMany(t => t.Configuration.Operations!.Select(o => new WorkAssignmentAdvancedSummaryNativeTargetPayload("records", t.Configuration.TargetId, o.OperationId))).ToArray();
            Check(refs.Length > 0, "second-section fixture has no native plan");
            var state = await f.Service().GetP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-next", default);
            var payload = Payload(refs) with { Sections = [new("part-next", false, [], refs)] };
            var saved = await f.Service().PutP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-next", Command(state, payload), default);
            var locked = await f.Service().LockP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-next", Command(saved, new { }), default);
            Check(main.Identity.ConfigId != locked.Identity.ConfigId && Canonical((await f.Read()).Payload) == Canonical(main.Payload),
                "second section overwrote first section");
            Check(Canonical(locked.Payload) == Canonical(payload) && await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.CountDocumentsAsync(_ => true) == 2,
                "section ownership or refs lost");
        });
        await test("Advanced native config and locked receipt survive actual Mongo restart", async () =>
        {
            var f = await Create(); var locked = await f.Lock(await f.Save()); await restart(); var read = await f.Read();
            Check(Canonical(read.Identity) == Canonical(locked.Identity) && Canonical(read.Payload) == Canonical(locked.Payload)
                && Canonical(read.ValidationReceipt!) == Canonical(locked.ValidationReceipt!), "restart changed native config");
        });
        await test("Advanced native next draft archive and history keep old operation order", async () =>
        {
            var f = await Create(); var locked = await f.Lock(await f.Save());
            var next = await f.Service().CreateNextP8DraftAsync(f.Assignment, f.Inner.Template.Id, "part-main", Command(locked, new { }), default);
            var reversed = Payload(f.References.Reverse().ToArray()); var changed = await f.Put(Command(next, reversed)); var newer = await f.Lock(changed);
            var history = await f.Service().GetP8ConfigVersionAsync(f.Assignment, f.Inner.Template.Id, "part-main", 1, default);
            Check(Canonical(history.Payload) == Canonical(locked.Payload) && history.Identity.ConfigHash == locked.Identity.ConfigHash,
                "history rebased to current selection");
            Check(newer.Identity.ConfigHash != locked.Identity.ConfigHash, "order did not affect dependency/config hash");
            var pool = await f.Inner.Ctx.WorkSummaryTokenLedgers.Find(l => l.RecordKind == WorkSummaryTokenLedgerRecordKinds.Pool).SingleAsync();
            Check(pool.UsedUnits == 1, "second lock did not consume one quota unit");
            var archived = await f.Service().ArchiveP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", Command(newer, new { }), default);
            Check(archived.Identity.Status == "ARCHIVED" && Canonical(archived.Payload) == Canonical(reversed), "archive lost refs");
        });
        await test("Advanced native concurrent identical command replays exactly once", async () =>
        {
            var f = await Create(); var body = Command(await f.Read(), Payload(f.References)); var service = f.Service();
            var outcomes = await Task.WhenAll(service.PutP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", body, default),
                service.PutP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", body, default));
            Check(Canonical(outcomes[0]) == Canonical(outcomes[1]) && await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.CountDocumentsAsync(_ => true) == 1
                && await f.Inner.Ctx.StatConfigCommandReceipts.CountDocumentsAsync(_ => true) == 1, "concurrent replay duplicated config or receipt");
        });
        await test("Advanced native stale CAS does not overwrite newer selection", async () =>
        {
            var f = await Create(); var state = await f.Read(); var saved = await f.Put(Command(state, Payload(f.References)));
            await Reject(() => f.Put(Command(state, Payload(f.References.Reverse().ToArray()))));
            Check(Canonical((await f.Read()).Payload) == Canonical(saved.Payload), "stale CAS overwrote native refs");
        });
        await test("Advanced native Form statistic drift blocks lock and preserves draft", async () =>
        {
            var f = await Create(); var saved = await f.Save();
            var before = (await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.Find(_ => true).SingleAsync()).ToBsonDocument().ToJson();
            await f.Inner.Ctx.DynamicFormTemplates.UpdateOneAsync(t => t.Id == f.Inner.Template.Id,
                Builders<DynamicFormTemplate>.Update.Inc(t => t.StatisticConfigRevision, 1));
            await Reject(() => f.Lock(saved));
            Check((await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.Find(_ => true).SingleAsync()).ToBsonDocument().ToJson() == before,
                "dependency failure partially locked config");
        });
        foreach (var fault in new[] { "hash", "json", "receipt" })
            await test("Advanced native corrupt stored " + fault + " is rejected and retained", async () =>
            {
                var f = await Create(); _ = await f.Lock(await f.Save());
                var rows = f.Inner.Ctx.Db.GetCollection<BsonDocument>(f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.CollectionNamespace.CollectionName);
                var update = fault switch { "hash" => new BsonDocument("configHash", new string('0', 64)),
                    "json" => new BsonDocument("configJson", "{}"), _ => new BsonDocument("validationReceiptHash", new string('0', 64)) };
                await rows.UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", update));
                var raw = (await rows.Find(_ => true).SingleAsync()).ToJson();
                await Reject(() => f.Read()); Check((await rows.Find(_ => true).SingleAsync()).ToJson() == raw, "corrupt config repaired");
            });
        await test("Advanced native authorization precedes config mutation", async () =>
        {
            var f = await Create(); var body = Command(await f.Read(), Payload(f.References));
            await Reject(() => f.Service("cccccccccccccccccccccccc").PutP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", body, default),
                "ADVANCED_SUMMARY_CONFIG_MANAGE_FORBIDDEN");
            Check(await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.CountDocumentsAsync(_ => true) == 0, "unauthorized config created");
        });
        await test("Advanced native Flow source remains outside this slice", async () =>
        {
            var f = await Create(); var payload = Payload(f.References) with { SourceScope = new("FLOW_FINAL", "cccccccccccccccccccccccc", null, null, "EFFECTIVE") };
            await Reject(async () => await f.Put(Command(await f.Read(), payload)), "ADVANCED_NATIVE_FLOW_CONSUMER_REQUIRED");
        });
        await test("Advanced scalar preview and hierarchy reject native before cache or enqueue", async () =>
        {
            var f = await Create(); var locked = await f.Lock(await f.Save());
            await Reject(() => f.Service().ListConfigsAsync(f.Assignment, f.Inner.Template.Id, "part-main", f.Inner.Actor, default), "ADVANCED_NATIVE_RESULT_CONSUMER_REQUIRED");
            var hierarchy = new WorkAssignmentAdvancedSummaryHierarchyService(f.Inner.Ctx, null!, f.Inner.Payload, null!, f.Inner.Activation, null!);
            await Reject(() => hierarchy.RequestDayNodeBuildAsync(locked.Identity.VersionId, "2026-09-17", new(), f.Inner.Actor, default), "ADVANCED_NATIVE_RESULT_CONSUMER_REQUIRED");
            Check(await f.Inner.Ctx.WorkAssignmentAdvancedSummaryDayNodes.CountDocumentsAsync(_ => true) == 0, "scalar hierarchy accepted native");
        });
        await test("Advanced native lock rolls back config and token together before commit", async () =>
        {
            var f = await Create(); var saved = await f.Save();
            var before = (await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.Find(_ => true).SingleAsync()).ToBsonDocument().ToJson();
            await Reject(() => f.Service(failBeforeCommit: true).LockP8ConfigAsync(f.Assignment, f.Inner.Template.Id, "part-main", Command(saved, new { }), default),
                "FIXTURE_PRECOMMIT_INTERRUPTION");
            Check((await f.Inner.Ctx.WorkAssignmentAdvancedSummaryConfigs.Find(_ => true).SingleAsync()).ToBsonDocument().ToJson() == before
                && await f.Inner.Ctx.WorkSummaryTokenLedgers.CountDocumentsAsync(_ => true) == 0, "lock/token partial commit");
            Check(await f.Inner.Ctx.StatConfigCommandReceipts.CountDocumentsAsync(_ => true) == 1, "failed lock left token or config receipt");
        });
        await test("Advanced native quota denial retains next draft and existing ledger", async () =>
        {
            var f = await Create(); var first = await f.Lock(await f.Save());
            var secondDraft = await f.Service().CreateNextP8DraftAsync(f.Assignment, f.Inner.Template.Id, "part-main", Command(first, new { }), default);
            var second = await f.Lock(secondDraft);
            var third = await f.Service().CreateNextP8DraftAsync(f.Assignment, f.Inner.Template.Id, "part-main", Command(second, new { }), default);
            var ledger = (await f.Inner.Ctx.WorkSummaryTokenLedgers.Find(_ => true).SortBy(l => l.Id).ToListAsync()).ToJson();
            try { await f.Lock(third); throw new Exception("Expected quota rejection"); }
            catch (AppException ex) { Check(ex.Code == AppErrorCode.WORK_SUMMARY_TOKEN_QUOTA_EXCEEDED, "wrong quota rejection"); }
            Check(Canonical((await f.Read()).Identity) == Canonical(third.Identity)
                && (await f.Inner.Ctx.WorkSummaryTokenLedgers.Find(_ => true).SortBy(l => l.Id).ToListAsync()).ToJson() == ledger,
                "quota rejection changed draft or ledger");
        });
        await test("Advanced scalar consumer also blocks native Form without explicit native refs", async () =>
        {
            var f = await Create(); var saved = await f.Put(Command(await f.Read(), Payload(null))); var locked = await f.Lock(saved);
            var hierarchy = new WorkAssignmentAdvancedSummaryHierarchyService(f.Inner.Ctx, null!, f.Inner.Payload, null!, f.Inner.Activation, null!);
            await Reject(() => hierarchy.RequestDayNodeBuildAsync(locked.Identity.VersionId, "2026-09-17", new(), f.Inner.Actor, default),
                "ADVANCED_NATIVE_RESULT_CONSUMER_REQUIRED");
        });
        await test("Advanced selected AVG resolves exact raw sum and count with same calculator", () =>
        {
            var input = Fixtures.LockedUnitInput();
            var plan = WorkAssignmentAdvancedSummaryConfigService.ResolveNativeAdvancedPlan(input.Template, "part-main", [new("a", "a-legacy", "a-legacy-1")]);
            var sources = input.Sources.Select(s => NativeStatisticCalculationSource.Capture(s.Report, s.Payload)).ToArray();
            var pins = sources.Select(s => s.Pin).ToArray();
            var result = NativeTableStatisticCalculator.Calculate(Canonical(plan),
                DynamicFormNativeTableDefinition.ReadStored(input.Template.NativeTablesVersion, input.Template.TablesJson)!,
                input.Template.SectionsJson, input.Template.FieldsJson, input.Template.BlocksJson, input.Template.PublishedSchemaHash!,
                pins, WorkReportNativeSourcePin.Digest(pins), sources, Fixtures.Limits);
            var avg = result.Groups.Single(g => g.Address.RowId == "a-r1").Operations.Single().Numeric!;
            Check(avg.Sum == "8" && avg.Count == 2, "selection changed raw AVG");
            return Task.CompletedTask;
        });
    }

    private static WorkAssignmentAdvancedSummaryConfigPayload Payload(IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload>? refs)
        => new(new("SELF", null, null, null, null), [new("part-main", false, [new("root-count", "NUMBER", "SUM")], refs)],
            ["DAY", "MONTH", "YEAR"], [], [], "Native table configuration");
    private static JsonElement Command(WorkAssignmentAdvancedSummaryConfigReadback state, object payload)
        => Fixtures.Json(new { commandId = Guid.NewGuid().ToString("N"), expectedRevision = state.Identity.Revision,
            expectedConfigHash = state.Identity.ConfigHash, payload });
    private static string Canonical(object value) => StatConfigCanonicalJson.Canonicalize(value);
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
    private static DynamicFormStatisticConfigCommandService.NativeStatisticInputView View(DynamicFormTemplate template)
        => DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(template, template.StatisticConfigId!, template.StatisticConfigVersionId!,
            template.StatisticConfigVersionNo, template.StatisticConfigRevision, template.StatisticConfigHash!);
    private sealed class Fixture(PublicationCases.Fixture inner)
    {
        internal PublicationCases.Fixture Inner => inner;
        internal string Assignment => inner.Reports[0].WorkAssignmentId;
        internal WorkAssignmentAdvancedSummaryNativeTargetPayload[] References => View(inner.Template).NativePlan!.Targets
            .Where(t => DynamicFormSectionSnapshotBuilder.GetRequiredSection(inner.Template, "part-main").NativeTableIds.Contains(t.Configuration.TableId!))
            .SelectMany(t => t.Configuration.Operations!.Select(o => new WorkAssignmentAdvancedSummaryNativeTargetPayload(t.Configuration.TableId, t.Configuration.TargetId, o.OperationId))).ToArray();
        internal WorkAssignmentAdvancedSummaryConfigService Service(string? actor = null, bool failBeforeCommit = false)
        {
            var http = new DefaultHttpContext();
            http.Items[MeAccessor.MeItemKey] = new MeResponse(actor ?? inner.Actor, "fixture", "fixture", [], "bbbbbbbbbbbbbbbbbbbbbbbb", null, null, null, [], null, false);
            IStatConfigTransactionRunner transactions = new StatConfigTransactionRunner(inner.Ctx, NullLogger<StatConfigTransactionRunner>.Instance);
            if (failBeforeCommit) transactions = new FailingTransaction(transactions);
            return new(inner.Ctx, null!, inner.Payload, null!, new WorkSummaryTokenService(inner.Ctx, transactions),
                new(new HttpContextAccessor { HttpContext = http }), transactions, inner.Activation);
        }
        internal Task<WorkAssignmentAdvancedSummaryConfigReadback> Read() => Service().GetP8ConfigAsync(Assignment, inner.Template.Id, "part-main", default);
        internal Task<WorkAssignmentAdvancedSummaryConfigReadback> Put(JsonElement command) => Service().PutP8ConfigAsync(Assignment, inner.Template.Id, "part-main", command, default);
        internal async Task<WorkAssignmentAdvancedSummaryConfigReadback> Save() => await Put(Command(await Read(), Payload(References)));
        internal Task<WorkAssignmentAdvancedSummaryConfigReadback> Lock(WorkAssignmentAdvancedSummaryConfigReadback state)
            => Service().LockP8ConfigAsync(Assignment, inner.Template.Id, "part-main", Command(state, new { }), default);
    }
    private sealed class FailingTransaction(IStatConfigTransactionRunner inner) : IStatConfigTransactionRunner
    {
        public Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct = default)
            => inner.ExecuteAsync<T>(async (session, token) => {
                _ = await operation(session, token);
                throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "FIXTURE_PRECOMMIT_INTERRUPTION" });
            }, ct);
    }
}
