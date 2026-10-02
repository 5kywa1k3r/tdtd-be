using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.NativeStatisticTests;

internal static class NativeMetadataCases
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, Func<Task> restart, string evidence)
    {
        var wireDirectory = Path.Combine(evidence, "consumer-wire");
        Directory.CreateDirectory(wireDirectory);
        void Wire(string name, object response) => File.WriteAllText(Path.Combine(wireDirectory, name + ".json"),
            JsonSerializer.Serialize(response, StatConfigCanonicalJson.StrictJsonOptions));
        await test("export typed calculator cases for isolated FE consumer", () => {
            foreach (var type in new[] { "number", "boolean", "plainText", "date", "fullDate", "singleSelect", "multiSelect", "stringList" })
            {
                var methods = new[] { "COUNT", "CONCAT", "STACK_ROWS", "STACK_COLUMNS" }
                    .Concat(type is "singleSelect" or "multiSelect" or "date" ? new[] { "BUCKET_COUNT" } : Array.Empty<string>()).ToArray();
                object? present = type switch { "number" => 0, "boolean" => false, "date" => "09/2026", "fullDate" => "17/09/2026",
                    "singleSelect" => "B", "multiSelect" => new[] { "B", "A" }, "stringList" => new[] { "A\r\n🌿", "B" }, _ => "Việt\n🌿" };
                var calculated = Fixtures.Calculate(type, methods, [present, null, Fixtures.Missing, present],
                    bucket: type == "date" ? "date" : "option", limits: Fixtures.Limits with { ChunkBytes = 2048 });
                var plan = JsonSerializer.Deserialize<DynamicFormNativeStatisticPlanDto>(Fixtures.Plan(methods, bucket: type == "date" ? "date" : "option"), StatConfigCanonicalJson.StrictJsonOptions)!;
                var table = Fixtures.Table(type);
                // Calculator fixture only: metadata is assembled from its explicit
                // table/plan, not a published P8 history. Reader fixtures below are separate.
                var target = new NativeStatisticTargetMetadata(plan.Targets!.Single(), StatConfigCanonicalJson.HashObject(table), [],
                    new(table.SectionId!, "Calculator fixture"), new(table.Id!, table.Name!, table.Order!.Value, table.Layout!, table.Fields!, table.Rows!),
                    table.Rows!.SelectMany(r => table.Fields!.Select(f => new NativeStatisticCellMetadata(f.Id!, r.Id!, table.TypeConfig!.Rules![0].Spec!))).ToArray());
                var metadata = new NativeStatisticResultMetadata(1, calculated.SchemaHash, calculated.PlanContentDigest,
                    StatConfigCanonicalJson.HashObject(new { version = 1, calculated.SchemaHash, calculated.PlanContentDigest, targets = new[] { target } }), [target]);
                Wire("typed-" + type, new { native = calculated, nativeMetadata = metadata });
            }
            return Task.CompletedTask;
        });
        var input = Fixtures.LockedUnitInput();
        var artifact = NativeStatisticGenerationStage.Prepare(input.Template, "111111111111111111111111", "period", input.Generation, input.Sources, Fixtures.Limits with { ChunkBytes = 2048 });
        var result = Convert<NativeStatisticResultDocument>(artifact.Result);
        (NativeStatisticResultDocument Result, NativeStatisticResultMetadata Metadata) Project(string? config = null, string? plan = null, NativeStatisticResultDocument? data = null, int? budget = null)
            => NativeStatisticResultProjection.Project(artifact.DefinitionJson, config ?? artifact.ConfigurationJson, plan, data ?? result, budget ?? NativeStatisticResultProjection.MaxMetadataBytes);
        await test("native metadata derives names axes types and options from pinned schema", () => {
            var p = Project(); var a = p.Metadata.Targets.Single(t => t.Configuration.TargetId == "a-legacy");
            Check(a.Section.Id == "part-main" && a.Section.Title == "Phần chính" && a.Table.Id == "a", "wrong section/table");
            Check(a.Table.Rows.Select(r => r.Id).SequenceEqual(a.Configuration.Selector!.RowIds!), "selector row order lost");
            Check(a.Cells.All(c => c.Spec.Type == "number") && a.Table.Fields.Single().Id == "a-amount", "wrong resolved types/axes");
            Check(p.Result.PlanContentDigest == p.Metadata.PlanContentDigest && p.Metadata.SchemaHash == result.SchemaHash, "digest mismatch");
            Check(p.Metadata.ContentHash == StatConfigCanonicalJson.HashObject(new { p.Metadata.Version, p.Metadata.SchemaHash, p.Metadata.PlanContentDigest, p.Metadata.Targets }), "wrong metadata hash");
            return Task.CompletedTask;
        });
        await test("native metadata keeps CONCAT separators methods and stack source results unchanged", () => {
            var before = Canonical(artifact); var p = Project();
            Wire("complete-result", new { native = p.Result, nativeMetadata = p.Metadata });
            var target = p.Metadata.Targets.Single(t => t.Configuration.TargetId == "a-text-plan");
            Check(target.Configuration.Operations!.Any(o => o.Options.GetProperty("separator").GetString() == "\r\n"), "CRLF lost");
            Check(target.Configuration.Operations!.Any(o => o.Options.GetProperty("separator").GetString() == "\\n"), "literal backslash-n lost");
            Check(Canonical(artifact) == before && Canonical(p.Result) == Canonical(result), "projection mutated values/artifact");
            return Task.CompletedTask;
        });
        await test("native metadata exposes only explicitly selected operations and axes", () => {
            var view = DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(input.Template, input.Template.StatisticConfigId!, input.Template.StatisticConfigVersionId!,
                input.Template.StatisticConfigVersionNo, input.Template.StatisticConfigRevision, input.Template.StatisticConfigHash!);
            var plan = DynamicFormNativeStatisticSelection.Select(view, [("a", "a-legacy", "a-legacy-1")], "refs", "test", (_, reason) => new Exception(reason));
            var pins = input.Sources.Select(s => WorkReportNativeSourcePin.Capture(s.Report, s.Payload)).ToArray();
            var calculated = NativeTableStatisticCalculator.Calculate(Canonical(plan), DynamicFormNativeTableDefinition.ReadStored(input.Template.NativeTablesVersion, input.Template.TablesJson)!,
                input.Template.SectionsJson, input.Template.FieldsJson, input.Template.BlocksJson, input.Template.PublishedSchemaHash!, pins, WorkReportNativeSourcePin.Digest(pins),
                input.Sources.Select(s => NativeStatisticCalculationSource.Capture(s.Report, s.Payload)), Fixtures.Limits);
            var p = Project(plan: Canonical(plan), data: Convert<NativeStatisticResultDocument>(calculated));
            Check(p.Metadata.Targets.Count == 1 && p.Metadata.Targets.Single().Configuration.Operations!.Single().Method == "AVG", "unselected operations exposed");
            Check(!Canonical(p.Metadata).Contains("b-amount") && !Canonical(p.Metadata).Contains("a-text-plan"), "unselected metadata leaked");
            return Task.CompletedTask;
        });
        foreach (var fault in new[] { "schema", "digest", "operation", "target", "config", "labels", "separator", "structure", "null-target", "quota" })
            await test("native metadata rejects " + fault + " without altering stored inputs", () => {
                var before = Canonical(artifact);
                var configuration = JsonNode.Parse(artifact.ConfigurationJson)!;
                var section = JsonNode.Parse(configuration["nativePlanSectionJson"]!.GetValue<string>())!;
                if (fault == "config") section["definition"]!["version"] = 5;
                if (fault == "labels") section["targets"]![0]!["labelSnapshots"] = new JsonArray(Fixtures.Json(new { code = "missing" }).GetRawText());
                if (fault == "structure") section["targets"]![0]!["structureHash"] = new string('a', 64);
                if (fault == "null-target") section["targets"]![0] = null;
                if (fault == "separator") section["targets"]!.AsArray().Single(t => t!["configuration"]!["targetId"]!.GetValue<string>() == "a-text-plan")!["configuration"]!["operations"]![0]!["options"]!["separator"] = "CHANGED";
                configuration["nativePlanSectionJson"] = section.ToJsonString();
                Reject(() => {
                    if (fault == "schema") NativeStatisticResultProjection.Project(artifact.DefinitionJson.Replace("Phần", "Other"), artifact.ConfigurationJson, null, result with { SchemaHash = new string('a', 64) });
                    else if (fault == "digest") Project(data: result with { PlanContentDigest = new string('a', 64) });
                    else if (fault is "operation" or "target") {
                        var group = result.Groups[0];
                        Project(data: result with { Groups = [fault == "target" ? group with { Address = group.Address with { TargetId = "missing" } }
                            : group with { Operations = [group.Operations[0] with { Method = "BOGUS" }] }] });
                    }
                    else Project(config: configuration.ToJsonString(), budget: fault == "quota" ? 1 : null);
                });
                Check(Canonical(artifact) == before, "raw input mutated"); return Task.CompletedTask;
            });
        await test("native metadata content identity does not masquerade as source freshness", () => {
            var changed = result with { Sources = result.Sources.Select(s => s with { PayloadRevision = s.PayloadRevision + 1 }).ToArray() };
            Check(Project().Metadata.ContentHash == Project(data: changed).Metadata.ContentHash, "metadata hash incorporates source clock");
            return Task.CompletedTask;
        });
        await test("hidden native target exposes neither values nor label metadata", async () => {
            var label = new NativeLabelCases.LabelState();
            async Task<tdtd_be.Models.DynamicFormTemplate> Hidden(tdtd_be.Data.MongoDbContext ctx)
            {
                var owner = await label.Configure(ctx);
                var tables = DynamicFormNativeTableDefinition.ReadStored(owner.NativeTablesVersion, owner.TablesJson)!
                    .Select(t => t with { StatisticPlan = t.StatisticPlan! with { Targets = t.StatisticPlan.Targets!.Select(s =>
                        s.IsStatistic == true ? s with { ShowInDetail = false, ShowInTree = false } : s).ToArray() } }).ToArray();
                return await NativeLabelCases.Patch(ctx, owner, tables);
            }
            var f = await BasicResultCases.Fixture.Create(connection, database + "_meta_hidden", "PERIOD_RANGE", "2026-09-17", "2026-09-18", Hidden);
            var response = await f.Read();
            Check(response.Native.Groups.Count == 0 && response.NativeMetadata!.Targets.Count == 0, "hidden values or config leaked");
            Check(!Canonical(response.NativeMetadata!).Contains("metric.amount"), "hidden label leaked");
        });
        foreach (var kind in new[] { "Basic", "Advanced" })
            await test(kind + " metadata retains real P8 label pins across snapshot restart and historical read", async () => {
                var label = new NativeLabelCases.LabelState();
                if (kind == "Basic")
                {
                    var f = await BasicResultCases.Fixture.Create(connection, database + "_meta_basic", "PERIOD_RANGE", "2026-09-17", "2026-09-18", label.Configure);
                    var first = await f.Read(); VerifyLabel(first.NativeMetadata!, label.Result.VersionId);
                    Wire("basic-current", first);
                    var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                    await restart();
                    Check(Canonical((await f.Read()).NativeMetadata!) == Canonical(first.NativeMetadata!), "metadata changed after restart");
                    await label.Update(f.Inner.Ctx, label.Payload with { Name = "Nhãn hiện hành khác", IsActive = false });
                    var history = await f.Service().GetNativeSummaryAsync(f.Request(first.SnapshotId) with { Historical = true }, default);
                    Wire("basic-history", history);
                    Check(Canonical(history.NativeMetadata!) == Canonical(first.NativeMetadata!) && history.Freshness == "HISTORICAL", "history rebound label");
                    Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw, "read projection rewrote snapshot");
                }
                else
                {
                    var f = await AdvancedResultCases.Fixture.Create(connection, database + "_meta_advanced", label.Configure);
                    var first = await f.Read(); VerifyLabel(first.NativeMetadata!, label.Result.VersionId);
                    Wire("advanced-current", first);
                    Check(first.NativeMetadata!.Targets.All(t => t.Table.Id is "a" or "b" && t.Section.Id == "part-main"), "outside section metadata leaked");
                    var raw = (await f.Rows.Find(_ => true).SingleAsync()).ToJson();
                    await restart();
                    Check(Canonical((await f.Read()).NativeMetadata!) == Canonical(first.NativeMetadata!), "metadata changed after restart");
                    await label.Update(f.Inner.Ctx, label.Payload with { Name = "Nhãn hiện hành khác", IsActive = false });
                    var history = await f.Service().GetNativeSummaryAsync(f.Request() with { Historical = true, SnapshotId = first.SnapshotId }, default);
                    Wire("advanced-history", history);
                    Check(Canonical(history.NativeMetadata!) == Canonical(first.NativeMetadata!), "history rebound label");
                    Check((await f.Rows.Find(_ => true).SingleAsync()).ToJson() == raw, "read projection rewrote snapshot");
                }
            });
        await test("Direct metadata keeps published label pins and artifact hash after restart", async () => {
            var label = new NativeLabelCases.LabelState();
            var f = await PublicationCases.Fixture.Create(connection, database + "_meta_direct", label.Configure);
            await f.Project(); var first = await f.Reader().ReadNativeAsync(f.Request(), default); VerifyLabel(first.NativeMetadata!, label.Result.VersionId);
            Wire("direct-current", first);
            var job = await f.Job(); var raw = job.ToJson();
            await restart(); var again = await f.Reader().ReadNativeAsync(f.Request(), default);
            Check(Canonical(again.NativeMetadata!) == Canonical(first.NativeMetadata!) && again.ArtifactHash == first.ArtifactHash, "Direct restart metadata changed");
            await label.Update(f.Ctx, label.Payload with { Name = "New live name", IsActive = false });
            var history = await f.Reader().ReadNativeAsync(f.Request() with { Historical = true, RunId = job.Id, GenerationId = job.GenerationId }, default);
            Wire("direct-history", history);
            Check(Canonical(history.NativeMetadata!) == Canonical(first.NativeMetadata!) && history.Metadata.Freshness == "HISTORICAL", "Direct history rebound metadata");
            Check((await f.Job()).ToJson() == raw, "projection mutated job");
            await f.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Reports[1].Id,
                Builders<tdtd_be.Models.WorkAssignmentReport>.Update.Set(r => r.PayloadUpdatedAtUtc, f.Reports[1].PayloadUpdatedAtUtc!.Value.AddSeconds(1)));
            var stale = await f.Reader().ReadNativeAsync(f.Request(), default);
            Wire("direct-stale", stale);
            Check(stale.Metadata.State == "STALE" && stale.Result is null && stale.NativeMetadata is null,
                "stale Direct response exposed metadata or results");
        });
    }
    private static void VerifyLabel(NativeStatisticResultMetadata metadata, string version)
    {
        var label = metadata.Targets.Single(t => t.Configuration.TargetId == "a-legacy").LabelSnapshots.Single();
        Check(label.VersionId == version && label.Code == "metric.amount" && label.Usage == "STATISTIC", "label pins missing/wrong");
    }
    private static T Convert<T>(object value) => JsonSerializer.Deserialize<T>(Canonical(value), StatConfigCanonicalJson.StrictJsonOptions)!;
    private static string Canonical(object value) => StatConfigCanonicalJson.Canonicalize(value);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (AppException) { return; } throw new Exception("Expected structured metadata rejection"); }
}
