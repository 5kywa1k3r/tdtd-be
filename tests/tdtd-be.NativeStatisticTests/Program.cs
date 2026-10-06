using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.CanvasRoundtripTests;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.NativeStatisticTests;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

var calculationOnly = args.Length == 2 && args[1] == "--calculation-only";
var comparisonOnly = args.Length == 3 && args[1] == "--comparison-only";
if (!calculationOnly && args.Length < 3) throw new ArgumentException("Expected evidence dbRoot mongod, or evidence --calculation-only.");
var evidence = Path.GetFullPath(args[0]);
var dbRoot = calculationOnly || comparisonOnly ? "" : Path.GetFullPath(args[1]);
var mongod = calculationOnly || comparisonOnly ? "" : Path.GetFullPath(args[2]);
Directory.CreateDirectory(evidence);
var results = new List<object>();
var failures = 0;
async Task Case(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; var details = ex is AppException app ? JsonSerializer.Serialize(app.Details) : null; results.Add(new { name, status = "failed", error = ex.ToString(), details }); Console.WriteLine("FAIL " + name + " " + details + " " + ex); }
}
void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}; got {actual}"); }
void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
void Reject(Action action)
{
    try { action(); } catch (Exception ex) when (ex is AppException or WorkReportDirectGenerationValidationException or OperationCanceledException) { return; }
    throw new Exception("Expected explicit rejection");
}
async Task RejectAsync(Func<Task> action)
{
    try { await action(); } catch (Exception ex) when (ex is AppException or WorkReportDirectGenerationValidationException or OperationCanceledException) { return; }
    throw new Exception("Expected explicit rejection");
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
NativeStatisticOperationResult Op(NativeStatisticCalculationResult result, int index = 0) => result.Groups.Single().Operations[index];
string Text(NativeStatisticOperationResult operation) => string.Concat(operation.TextChunks!.OrderBy(c => c.Index).Select(c => c.Text));

if (args.Contains("--basic-worker-only"))
{
    CanvasMongo? isolated = null;
    try
    {
        isolated = await CanvasMongo.StartAsync(dbRoot, mongod);
        async Task Restart()
        {
            await isolated!.DisposeAsync(); isolated = await CanvasMongo.StartAsync(dbRoot, mongod);
        }
        await BasicWorkerCases.Run(Case, isolated.Connection, isolated.Database, Restart);
    }
    finally { if (isolated is not null) await isolated.DisposeAsync(); }
    await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
    return failures == 0 ? 0 : 1;
}

if (comparisonOnly)
{
    await NativeComparisonCases.Run(Case, Path.GetFullPath(args[2]));
    await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
    return failures == 0 ? 0 : 1;
}

if (args.Length == 4 && args[3] is "--advanced-only" or "--capture-comparison-only")
{
    CanvasMongo? isolated = null;
    try
    {
        isolated = await CanvasMongo.StartAsync(dbRoot, mongod);
        async Task RestartAdvanced()
        {
            await isolated!.DisposeAsync(); isolated = null;
            isolated = await CanvasMongo.StartAsync(dbRoot, mongod);
        }
        if (args[3] == "--capture-comparison-only") await AdvancedComparisonCases.Run(Case, isolated.Connection, isolated.Database);
        else await AdvancedResultCases.Run(Case, isolated.Connection, isolated.Database, RestartAdvanced);
    }
    finally { if (isolated is not null) await isolated.DisposeAsync(); }
    await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
    return failures == 0 ? 0 : 1;
}

if (args.Length == 4 && args[3] is "--http-only" or "--publish-only")
{
    try
    {
        await using var isolated = await CanvasMongo.StartAsync(dbRoot, mongod);
        if (args[3] == "--publish-only") await NativeFormPublishCases.Run(Case, isolated.Connection, isolated.Database, evidence);
        else await NativeHttpCases.Run(Case, isolated.Connection, isolated.Database, evidence);
    }
    catch (Exception ex) { failures++; results.Add(new { name = "HTTP setup", status = "failed", error = ex.ToString() }); Console.WriteLine(ex); }
    await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"RESULT {results.Count - failures} passed, {failures} failed");
    return failures == 0 ? 0 : 1;
}

foreach (var (method, expected) in new[] { ("COUNT", "4"), ("DISTINCT_COUNT", "3"), ("SUM", "12"), ("MIN", "0"), ("MAX", "6"), ("LATEST", "6") })
    await Case("numeric " + method, () => Sync(() => Equal(Op(Fixtures.Calculate("number", [method], [0, 3, 3, 6], reports: method == "LATEST" ? "sourceUpdatedAtAsc" : "reportIdAsc")).Value!.Text, expected)));
await Case("AVG exact sum/count across reports", () => Sync(() => {
    var result = Fixtures.Calculate("number", ["AVG"], [], sources: [Fixtures.Source("number", [2, 4, null, Fixtures.Missing]), Fixtures.Source("number", [6, 8, 10, 12], 2)]);
    Equal(Op(result).Numeric!.Sum, "42"); Equal(Op(result).Numeric!.Count, 6L);
}));
await Case("SUM preserves small decimal with large operand", () => Sync(() => {
    var result = Fixtures.Calculate("number", ["SUM"], [9007199254740991m, 0.0000000000000000000000000001m, 0, null]);
    Equal(Op(result).Numeric!.Sum, "9007199254740991.0000000000000000000000000001");
}));
await Case("missing null zero counted separately", () => Sync(() => {
    var result = Op(Fixtures.Calculate("number", ["COUNT"], [0, null, Fixtures.Missing, 2]));
    Equal(result.Value!.Text, "2"); Equal(result.Counts.MissingCount, 1L); Equal(result.Counts.NullCount, 1L);
}));
foreach (var method in new[] { "TRUE_COUNT", "FALSE_COUNT" })
    await Case(method, () => Sync(() => Equal(Op(Fixtures.Calculate("boolean", [method], [true, false, false, null])).Value!.Text, method == "TRUE_COUNT" ? "1" : "2")));
await Case("multiSelect cell count and option buckets", () => Sync(() => {
    var result = Fixtures.Calculate("multiSelect", ["COUNT", "BUCKET_COUNT"], [new[] { "B", "A" }, new[] { "A" }, Array.Empty<string>(), null]);
    Equal(Op(result).Value!.Text, "3"); Equal(Op(result, 1).Buckets!.Single(b => b.Code == "A").Count, 2L);
    Equal(Op(result, 1).Buckets!.Single(b => b.Code == "B").Label, "Nhãn B");
}));
await Case("singleSelect typed count buckets and labels", () => Sync(() => {
    var result = Fixtures.Calculate("singleSelect", ["COUNT", "BUCKET_COUNT", "CONCAT"], ["B", "A", "A", null], concatOptions: Fixtures.ConcatOptions("\n", choiceFormat: "label"));
    Equal(Op(result).Value!.Text, "3"); Equal(Op(result, 1).Buckets!.Single(b => b.Code == "A").Count, 2L);
    Equal(Text(Op(result, 2)), "Nhãn B\nNhãn A\nNhãn A");
}));
foreach (var type in new[] { "date", "fullDate" })
    await Case(type + " MIN MAX", () => Sync(() => {
        var result = Fixtures.Calculate(type, ["MIN", "MAX"], ["17/09/2026", "01/01/2026", null, "01/01/2027"]);
        Equal(Op(result).Value!.Text, "01/01/2026"); Equal(Op(result, 1).Value!.Text, "01/01/2027");
    }));
await Case("date bucket literals", () => Sync(() => Equal(Op(Fixtures.Calculate("date", ["BUCKET_COUNT"], ["2026", "2026", "2027", null], bucket: "date")).Buckets!.Single(b => b.Code == "2026").Count, 2L)));
await Case("CONCAT date retains literal precision", () => Sync(() => Equal(Text(Op(Fixtures.Calculate("date", ["CONCAT"], ["2026", "09/2026", "17/09/2026", null]))), "2026\n09/2026\n17/09/2026")));
await Case("richText not silently stripped for CONCAT", () => Sync(() => Reject(() => Fixtures.Calculate("richText", ["CONCAT"], ["<b>A</b>", null, null, null]))));
await Case("mixed date precision fails", () => Sync(() => Reject(() => Fixtures.Calculate("date", ["MIN"], ["2026", "09/2026", null, null]))));
foreach (var separator in new[] { "\n", "\r\n", "\\n", "", " 🌿 Việt\n" })
    await Case("CONCAT literal " + JsonSerializer.Serialize(separator), () => Sync(() => {
        var result = Op(Fixtures.Calculate("plainText", ["CONCAT"], ["A\r\n🌿", "", "A\r\n🌿", "B"], concatOptions: Fixtures.ConcatOptions(separator)));
        Equal(Text(result), string.Join(separator, new[] { "A\r\n🌿", "", "A\r\n🌿", "B" }));
        Equal(result.SegmentCount, 4L); Equal(result.TextSegments![1].LengthUtf16, 0);
        Equal(Text(result).Substring((int)result.TextSegments![2].StartUtf16, result.TextSegments[2].LengthUtf16), "A\r\n🌿");
    }));
await Case("CONCAT long full content across Unicode chunks", () => Sync(() => {
    var longText = string.Concat(Enumerable.Repeat("Việt 🌿\r\n", 100));
    var result = Op(Fixtures.Calculate("plainText", ["CONCAT"], [longText, longText, longText, longText]));
    Equal(Text(result), string.Join("\n", Enumerable.Repeat(longText, 4))); Assert(result.TextChunks!.Count > 5);
}));
await Case("CONCAT list item separator", () => Sync(() => Equal(Text(Op(Fixtures.Calculate("stringList", ["CONCAT"], [new[] { "A", "B" }, Array.Empty<string>(), new[] { "C" }, null], concatOptions: Fixtures.ConcatOptions("\n", "\r\n")))), "A\r\nB\n\nC")));
await Case("CONCAT choice labels retain item order", () => Sync(() => Equal(Text(Op(Fixtures.Calculate("multiSelect", ["CONCAT"], [new[] { "B", "A" }, new[] { "A" }, null, null], concatOptions: Fixtures.ConcatOptions("\n", ";", choiceFormat: "label")))), "Nhãn B;Nhãn A\nNhãn A")));
await Case("CONCAT boolean false and numeric zero are values", () => Sync(() => {
    Equal(Text(Op(Fixtures.Calculate("boolean", ["CONCAT"], [false, true, null, null]))), "false\ntrue");
    Equal(Text(Op(Fixtures.Calculate("number", ["CONCAT"], [0, 2.50m, null, null]))), "0\n2.5");
}));
await Case("CONCAT blank skip and distinct", () => Sync(() => Equal(Text(Op(Fixtures.Calculate("plainText", ["CONCAT"], ["A", " ", "A", "B"], concatOptions: Fixtures.ConcatOptions("\n", blankPolicy: "skip", duplicatePolicy: "distinct")))), "A\nB")));
foreach (var (grouping, expected) in new[] { ("selection", "20"), ("row", "6,14"), ("column", "8,12"), ("cell", "2,4,6,8") })
    await Case("group " + grouping, () => Sync(() => Equal(string.Join(",", Fixtures.Calculate("number", ["SUM"], [2, 4, 6, 8], grouping).Groups.Select(g => g.Operations[0].Value!.Text)), expected)));
await Case("record grouping does not join equal record IDs across reports", () => Sync(() => {
    var result = Fixtures.Calculate("number", ["SUM"], [], "record", "vertical", sources: [Fixtures.Source("number", [2, 4, 6, 8], layout: "vertical"), Fixtures.Source("number", [10, 12, 14, 16], 2, "vertical")]);
    Equal(result.Groups.Count, 4); Equal(result.Groups.Select(g => g.Address.ReportId).Distinct().Count(), 2);
}));
foreach (var method in new[] { "STACK_ROWS", "STACK_COLUMNS" })
    await Case(method + " geometry and sources", () => Sync(() => {
        var result = Op(Fixtures.Calculate("number", [method], [], sources: [Fixtures.Source("number", [2, 4, 6, 8]), Fixtures.Source("number", [10, 12, 14, 16], 2)], limits: Fixtures.Limits with { ChunkBytes = 2048 }));
        Equal(result.Stack!.Rows, method == "STACK_ROWS" ? 4L : 2L); Equal(result.Stack.Columns, method == "STACK_ROWS" ? 2L : 4L);
        Equal(result.Stack.Chunks.SelectMany(c => c.Cells).Count(), 8); Equal(result.Stack.Blocks.Count, 2);
    }));
await Case("source order independent of enumeration", () => Sync(() => {
    var a = Fixtures.Source("plainText", ["A", "B", null, null]); var b = Fixtures.Source("plainText", ["C", "D", null, null], 2);
    Equal(Text(Op(Fixtures.Calculate("plainText", ["CONCAT"], [], sources: [b, a]))), "A\nB\nC\nD");
}));
await Case("LATEST uses captured time before report ID", () => Sync(() => {
    var a = Fixtures.Source("number", [1, 2, null, null], 1, time: new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));
    var b = Fixtures.Source("number", [5, 6, null, null], 2);
    Equal(Op(Fixtures.Calculate("number", ["LATEST"], [], sources: [a, b], reports: "sourceUpdatedAtAsc")).Value!.Text, "2");
}));
foreach (var fault in new[] { "missing", "duplicate", "drift", "timestamp", "cancel", "type", "option", "limit" })
    await Case("calculation rejects " + fault, () => Sync(() => {
        if (fault == "type") { Reject(() => Fixtures.Calculate("number", ["SUM"], ["3", 1, 2, 4])); return; }
        if (fault == "option") { Reject(() => Fixtures.Calculate("singleSelect", ["COUNT"], ["MISSING", "A", null, null])); return; }
        if (fault == "limit") { Reject(() => Fixtures.Calculate("number", ["SUM"], [1, 2, 3, 4], limits: Fixtures.Limits with { MaxCellVisits = 1 })); return; }
        var s = Fixtures.Source("number", [1, 2, 3, 4]); var pin = WorkReportNativeSourcePin.Capture(s.Report, s.Payload);
        var captured = NativeStatisticCalculationSource.Capture(s.Report, s.Payload);
        var pins = new[] { fault == "drift" ? pin with { PayloadRevision = 2 } : fault == "timestamp" ? pin with { PayloadUpdatedAtUtc = null } : pin };
        Reject(() => NativeTableStatisticCalculator.Calculate(Fixtures.Plan(["SUM"], reports: "sourceUpdatedAtAsc"), [Fixtures.Table()], Fixtures.Sections,
            "[]", "[]", Fixtures.Schema, pins, WorkReportNativeSourcePin.Digest(pins),
            fault == "missing" ? [] : fault == "duplicate" ? [captured, captured] : [captured], Fixtures.Limits,
            fault == "cancel" ? new CancellationToken(true) : default));
    }));
foreach (var policy in new[] { "EXCLUDE", "flow", "{\"defaultMode\":\"EXCLUDE\"}", "{\"rules\":[{}]}", "{\"defaultMode\":\"INCLUDE\",\"defaultMode\":\"EXCLUDE\"}" })
    await Case("contribution rejects " + policy, () => Sync(() => Reject(() => NativeStatisticGenerationStage.RequireContribution(
        new(policy == "EXCLUDE" ? "EXCLUDE" : "INCLUDE", policy == "flow"), policy.StartsWith('{') ? policy : null))));
await Case("draft cannot enter published native intake", () => Sync(() => Reject(() => DynamicFormStatisticConfigCommandService.ReadNativeStatisticInputView(new DynamicFormTemplate { Id = "111111111111111111111111" }, "", "", 1, 1, Fixtures.Schema))));
await Case("unit locked P8 v2 intake retains complete config and calculates", () => Sync(() => {
    var f = Fixtures.LockedUnitInput();
    var result = NativeStatisticGenerationStage.Prepare(f.Template, "111111111111111111111111", "period", f.Generation, f.Sources, Fixtures.Limits with { ChunkBytes = 2048 });
    Equal(result.Result.Groups.Single(g => g.Address.TableId == "a" && g.Address.TargetId == "a-legacy" && g.Address.RowId == "a-r1").Operations.Single(o => o.Method == "SUM").Value!.Text, "8");
    Assert(result.ConfigurationJson.Contains("nativePlanSectionJson")); Equal(result.Sources.Count, 2);
    NativeStatisticGenerationStage.Revalidate(result, f.Template, f.Sources);
}));
await Case("unit legacy P804 still rejects native owner", () => Sync(() => {
    var f = Fixtures.LockedUnitInput(); Reject(() => DynamicFormStatisticConfigCommandService.GetP804TrustedPersistedView(f.Template));
}));
foreach (var fault in new[] { "config-hash", "plan-snapshot", "source-time", "lifecycle", "excluded", "source-set" })
    await Case("unit stage revalidation rejects " + fault, () => Sync(() => {
        var f = Fixtures.LockedUnitInput();
        var limits = Fixtures.Limits with { ChunkBytes = 2048 };
        var result = NativeStatisticGenerationStage.Prepare(f.Template, "111111111111111111111111", "period", f.Generation, f.Sources, limits);
        if (fault == "config-hash") f.Template.StatisticConfigHash = new string('b', 64);
        if (fault == "plan-snapshot") f.Template.StatisticConfigSnapshots!.Single(s => s.VersionId == f.Template.StatisticConfigVersionId).Sections.NativePlanSectionJson = "{}";
        if (fault == "source-time") { f.Sources[0].Report.PayloadUpdatedAtUtc = f.Sources[0].Report.PayloadUpdatedAtUtc!.Value.AddSeconds(1); f.Sources[0] = (f.Sources[0].Report, f.Sources[0].Payload with { SourcePayloadUpdatedAtUtc = f.Sources[0].Report.PayloadUpdatedAtUtc }); }
        if (fault == "lifecycle") f.Sources[0].Report.LifecycleRevision++;
        if (fault == "excluded") f.Sources[0].Report.CumulativeContributionMode = "EXCLUDE";
        Reject(() => NativeStatisticGenerationStage.Revalidate(result, f.Template, fault == "source-set" ? f.Sources.Take(1).ToArray() : f.Sources));
    }));

await NativeEmptyPlanContentCases.Run(Case);

if (calculationOnly)
{
    await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(
        new { mode = "calculation-only", passed = results.Count - failures, failed = failures, cases = results },
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"RESULT {results.Count - failures} passed, {failures} failed (no Mongo/HTTP/job)");
    return failures == 0 ? 0 : 1;
}

// Storage tests operate on synthetic calculator artifacts only. They do not fake
// publication of a native Form or claim the production job/ACL path has run.
CanvasMongo? mongo = null;
try
{
    mongo = await CanvasMongo.StartAsync(dbRoot, mongod);
    IMongoDatabase Database() => new MongoClient(mongo.Connection).GetDatabase(mongo.Database);
    var store = new NativeStatisticArtifactStore(Database());
    var raw = Database().GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName);
    var storageLimits = new NativeStatisticStorageLimits(16_000_000, 1024, 10000);
    var artifact = Fixtures.Artifact();
    NativeStatisticArtifactReceipt? receipt = null;
    await Case("Mongo full typed artifact and independent byte hash", async () => {
        receipt = await store.StageAsync(artifact, storageLimits);
        var loaded = await store.ReadAsync(receipt, storageLimits);
        Equal(StatConfigCanonicalJson.Canonicalize(loaded), StatConfigCanonicalJson.Canonicalize(artifact));
        var data = await raw.Find(new BsonDocument("kind", "chunk")).Sort(new BsonDocument("index", 1)).ToListAsync();
        var bytes = data.SelectMany(d => d["data"].AsBsonBinaryData.Bytes).ToArray();
        Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), receipt.ArtifactHash);
        Equal(Text(loaded.Result.Groups[0].Operations[0]), Text(artifact.Result.Groups[0].Operations[0]));
        Equal(loaded.Result.Groups[0].Operations[1].Stack!.Chunks.SelectMany(c => c.Cells).Count(), 8);
        await File.WriteAllTextAsync(Path.Combine(evidence, "receipt.json"), JsonSerializer.Serialize(receipt));
    });
    await Case("unit intake calculate Mongo readback then source revalidation", async () => {
        var f = Fixtures.LockedUnitInput(50); var loads = 0;
        var r = await NativeStatisticGenerationStage.StageAndVerifyAsync(store, "111111111111111111111111", "period", f.Generation,
            Fixtures.Limits with { ChunkBytes = 2048 }, storageLimits,
            _ => { loads++; return Task.FromResult(new NativeStatisticStageInputs(f.Template, f.Sources)); });
        Equal(loads, 2); Equal((await store.ReadAsync(r, storageLimits)).Result.Groups.Count > 0, true);
    });
    await Case("unit intake refuses changed sources after persisted stage", async () => {
        var f = Fixtures.LockedUnitInput(51); var loads = 0;
        await RejectAsync(() => NativeStatisticGenerationStage.StageAndVerifyAsync(store, "111111111111111111111111", "period", f.Generation,
            Fixtures.Limits with { ChunkBytes = 2048 }, storageLimits, _ => {
                if (++loads == 2) f.Sources[0].Report.LifecycleRevision++;
                return Task.FromResult(new NativeStatisticStageInputs(f.Template, f.Sources)); }));
        Equal(loads, 2);
    });
    await Case("Mongo exact replay no additional rows", async () => {
        var before = await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        var again = await store.StageAsync(artifact, storageLimits);
        Equal(again, receipt!); Equal(await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty), before);
    });
    await Case("Mongo changed config cannot overwrite same generation", async () => {
        var before = (await raw.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson();
        await RejectAsync(() => store.StageAsync(artifact with { ConfigurationJson = "{}" }, storageLimits));
        Equal((await raw.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson(), before);
    });
    await Case("Mongo restart retains exact artifact", async () => {
        var pid = mongo.Pid;
        await mongo.DisposeAsync(); mongo = null;
        mongo = await CanvasMongo.StartAsync(dbRoot, mongod); Assert(pid != mongo.Pid);
        store = new NativeStatisticArtifactStore(Database()); raw = Database().GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName);
        Equal(StatConfigCanonicalJson.Canonicalize(await store.ReadAsync(receipt!, storageLimits)), StatConfigCanonicalJson.Canonicalize(artifact));
    });
    await Case("Mongo different receipt hash refused", () => RejectAsync(() => store.ReadAsync(receipt! with { ArtifactHash = new string('b', 64) }, storageLimits)));
    await Case("Mongo smaller explicit read budget refused", () => RejectAsync(() => store.ReadAsync(receipt!, storageLimits with { MaxArtifactBytes = 10 })));
    await Case("Mongo over-budget stage writes nothing", async () => {
        var before = await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        await RejectAsync(() => store.StageAsync(Fixtures.Artifact(2), storageLimits with { MaxArtifactBytes = 10 }));
        Equal(await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty), before);
    });
    await Case("Mongo concurrent identical stage converges", async () => {
        var a = Fixtures.Artifact(3); var both = await Task.WhenAll(store.StageAsync(a, storageLimits), store.StageAsync(a, storageLimits));
        Equal(both[0], both[1]); _ = await store.ReadAsync(both[0], storageLimits);
    });
    await Case("Mongo newer generation preserves historical artifact", async () => {
        _ = await store.StageAsync(Fixtures.Artifact(4), storageLimits);
        Equal(StatConfigCanonicalJson.Canonicalize(await store.ReadAsync(receipt!, storageLimits)), StatConfigCanonicalJson.Canonicalize(artifact));
    });
    await Case("Mongo exact SUM and AVG state remain decimal strings", async () => {
        var sources = new[] { Fixtures.Source("number", [9007199254740991m, 0.0000000000000000000000000001m, 0, null]) };
        var numeric = Fixtures.Calculate("number", ["SUM", "AVG"], [], sources: sources);
        var a = Fixtures.Artifact(22) with { Result = numeric,
            Generation = Fixtures.Context(22.ToString("x24"), new string('c', 64), sources),
            Sources = sources.Select(s => new NativeStatisticStageSource(s.Report.Id, s.Report.WorkAssignmentId, s.Report.LifecycleRevision, null,
                WorkReportNativeSourcePin.Capture(s.Report, s.Payload))).ToArray(),
            DefinitionJson = JsonSerializer.Serialize(new { tables = new[] { Fixtures.Table() } }, StatConfigCanonicalJson.StrictJsonOptions),
            ConfigurationJson = Fixtures.Plan(["SUM", "AVG"]) };
        var r = await store.StageAsync(a, storageLimits);
        var loaded = await store.ReadAsync(r, storageLimits);
        Equal(Op(loaded.Result).Numeric!.Sum, "9007199254740991.0000000000000000000000000001");
        Equal(Op(loaded.Result, 1).Numeric!.Count, 3L);
    });
    await Case("Mongo pending manifest not readable then exact resume", async () => {
        var a = Fixtures.Artifact(5); var r = await store.StageAsync(a, storageLimits);
        await raw.UpdateOneAsync(new BsonDocument { { "runId", r.RunId }, { "kind", "manifest" } }, Builders<BsonDocument>.Update.Set("state", "STAGING"));
        await RejectAsync(() => store.ReadAsync(r, storageLimits));
        Equal(await store.StageAsync(a, storageLimits), r); _ = await store.ReadAsync(r, storageLimits);
    });
    await Case("Mongo partial chunk write resumes without replacing existing chunks", async () => {
        // Seed a separate owned DB with the intent and half of the exact bytes;
        // keep every original row as evidence, no deletes or restore in a live DB.
        var partialDb = new MongoClient(mongo.Connection).GetDatabase(mongo.Database + "_partial");
        var partialRaw = partialDb.GetCollection<BsonDocument>(NativeStatisticArtifactStore.CollectionName);
        var sourceHeader = await raw.Find(new BsonDocument { { "runId", receipt!.RunId }, { "kind", "manifest" } }).SingleAsync();
        var header = sourceHeader.DeepClone().AsBsonDocument; header["state"] = "STAGING";
        var chunks = await raw.Find(new BsonDocument("manifestId", sourceHeader["_id"])).Sort(new BsonDocument("index", 1)).ToListAsync();
        await partialRaw.InsertManyAsync(new[] { header }.Concat(chunks.Take(chunks.Count / 2)));
        var partialStore = new NativeStatisticArtifactStore(partialDb);
        await RejectAsync(() => partialStore.ReadAsync(receipt, storageLimits));
        Equal(await partialStore.StageAsync(artifact, storageLimits), receipt);
        Equal(await partialRaw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty), receipt.ChunkCount + 1L);
        Equal(StatConfigCanonicalJson.Canonicalize(await partialStore.ReadAsync(receipt, storageLimits)), StatConfigCanonicalJson.Canonicalize(artifact));
    });
    await Case("Mongo conflicting concurrent stages accept only one intent", async () => {
        var a = Fixtures.Artifact(20); var b = a with { ConfigurationJson = "{}" };
        async Task<NativeStatisticArtifactReceipt?> Attempt(NativeStatisticGenerationArtifact value)
        { try { return await store.StageAsync(value, storageLimits); } catch (AppException) { return null; } }
        var outcomes = await Task.WhenAll(Attempt(a), Attempt(b));
        Equal(outcomes.Count(o => o is not null), 1);
        var loaded = await store.ReadAsync(outcomes.Single(o => o is not null)!, storageLimits);
        Equal(loaded.ConfigurationJson, outcomes[0] is null ? b.ConfigurationJson : a.ConfigurationJson);
    });
    await Case("Mongo cancellation before stage writes nothing", async () => {
        var count = await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        await RejectAsync(() => store.StageAsync(Fixtures.Artifact(21), storageLimits, new CancellationToken(true)));
        Equal(await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty), count);
    });
    await Case("Mongo invalid generation pin writes nothing", async () => {
        var count = await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        await RejectAsync(() => store.StageAsync(artifact with { Generation = artifact.Generation with { StageLockSha256 = "missing" } }, storageLimits));
        Equal(await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty), count);
    });
    foreach (var kind in new[] { "hash", "index", "bytes", "extra", "missing" })
        await Case("Mongo corrupt completed artifact " + kind + " preserved", async () => {
            var a = Fixtures.Artifact(10 + Array.IndexOf(new[] { "hash", "index", "bytes", "extra", "missing" }, kind));
            var r = await store.StageAsync(a, storageLimits);
            var header = await raw.Find(new BsonDocument { { "runId", r.RunId }, { "kind", "manifest" } }).SingleAsync();
            var chunk = await raw.Find(new BsonDocument("manifestId", header["_id"])).FirstAsync();
            var update = kind switch {
                "hash" => Builders<BsonDocument>.Update.Set("sha256", new string('0', 64)),
                "index" => Builders<BsonDocument>.Update.Set("index", -1),
                "bytes" => Builders<BsonDocument>.Update.Set("data", new BsonBinaryData([1, 2, 3])),
                "extra" => Builders<BsonDocument>.Update.Set("unexpected", true),
                // Keep the damaged row as raw evidence; change its manifest address.
                _ => Builders<BsonDocument>.Update.Set("manifestId", "orphaned-test-evidence") };
            await raw.UpdateOneAsync(new BsonDocument("_id", chunk["_id"]), update);
            var before = (await raw.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson();
            await RejectAsync(() => store.ReadAsync(r, storageLimits));
            await RejectAsync(() => store.StageAsync(a, storageLimits));
            Equal((await raw.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync()).ToJson(), before);
        });
    async Task RestartMongo()
    {
        await mongo!.DisposeAsync(); mongo = null;
        mongo = await CanvasMongo.StartAsync(dbRoot, mongod);
    }
    await NativeReferenceCases.Run(Case, mongo.Connection, mongo.Database);
    await BasicConfigCases.Run(Case, mongo.Connection, mongo.Database, RestartMongo);
    await BasicResultCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await BasicWorkerCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await AdvancedConfigCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await AdvancedResultCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await NativeMetadataCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo, evidence);
    await NativeLabelCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await RefreshRecoveryCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await PublicationCases.Run(Case, mongo!.Connection, mongo.Database, RestartMongo);
    await NativeHttpCases.Run(Case, mongo!.Connection, mongo.Database, evidence);
    await NativeFormPublishCases.Run(Case, mongo!.Connection, mongo.Database, evidence);
}
catch (Exception ex) { failures++; results.Add(new { name = "storage setup", status = "failed", error = ex.ToString() }); Console.WriteLine(ex); }
finally { if (mongo is not null) await mongo.DisposeAsync(); }
await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {results.Count - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;
