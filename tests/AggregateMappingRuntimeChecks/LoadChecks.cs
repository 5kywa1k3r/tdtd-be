using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.DynamicFlows;

// Exploratory single-client measurements, never a production capacity or P95 claim.
internal static class LoadChecks
{
    internal static async Task Run(MongoDbContext db, AggregateMongoStore store, RuntimeFixture fixture,
        AggregateRecipeDto recipe, AggregatePeriodContextDto context, HttpClient http, Func<string> token,
        string run, CancellationToken ct)
    {
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS load " + name); }
        var payload = new WorkReportPayloadService(db);
        var templateAssignment = await db.WorkAssignments.Find(a => a.Id == fixture.Source.WorkAssignmentId).SingleAsync(ct);
        var templatePeriod = await db.WorkReportPeriods.Find(p => p.Id == fixture.Source.WorkReportPeriodId).SingleAsync(ct);
        var templateBinding = await db.WorkTemplateAssignees.Find(b => b.Id == templatePeriod.WorkTemplateAssigneeId).SingleAsync(ct);
        T Clone<T>(T source) => BsonSerializer.Deserialize<T>(source!.ToBson());
        string Id() => ObjectId.GenerateNewId().ToString();
        async Task<JsonElement> Send(string path, object body, bool allowBudget = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/aggregate-v2/" + path)
                { Content = JsonContent.Create(body, options: AggregateCanonical.Json) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
            using var response = await http.SendAsync(request, ct);
            var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
            if (!response.IsSuccessStatusCode && !(allowBudget && result.TryGetProperty("code", out var code)
                && (code.GetString() == "AGG_BUDGET_EXCEEDED" || code.GetString() == "AGG_PREVIEW_TIMEOUT")))
                throw new InvalidOperationException($"Load {path}: {(int)response.StatusCode} {result}");
            return result;
        }
        async Task<string> Snapshot()
        {
            var rows = new List<string>();
            foreach (var name in AggregateCollections.All)
                rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", fixture.WorkId))
                    .Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(x => x.ToJson()));
            rows.AddRange((await db.WorkAssignmentReports.Find(r => r.WorkId == fixture.WorkId).SortBy(r => r.Id).ToListAsync(ct)).Select(x => x.ToJson()));
            var current = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
            rows.Add((await payload.LoadReportPayloadAsync(current, ct)).ToJson());
            return AggregateDigest.Of(rows);
        }
        var count = 1;
        foreach (var size in new[] { 100, 166, 1000 })
        {
            var seeded = Stopwatch.StartNew();
            while (count < size)
            {
                var assignment = Clone(templateAssignment); assignment.Id = Id(); assignment.Code = run + "-load-" + count;
                assignment.Path = "/" + fixture.Binding.WorkAssignmentId + "/" + assignment.Id;
                var childUser = Id(); var childUnit = Id();
                await db.Users.InsertOneAsync(new AppUser { Id = childUser, Username = run + "-load-" + count,
                    UnitId = childUnit, CreatedByUserId = fixture.Actor }, cancellationToken: ct);
                assignment.Assignees = [new UserRef { UserId = childUser, UnitId = childUnit }];
                assignment.TargetUnitIds = [childUnit];
                var binding = Clone(templateBinding); binding.Id = Id(); binding.WorkAssignmentId = assignment.Id;
                binding.AssigneeUserId = childUser; binding.AssigneeUnitId = childUnit;
                var period = Clone(templatePeriod); period.Id = Id(); period.WorkAssignmentId = assignment.Id;
                period.AssigneeUserId = childUser; period.AssigneeUnitId = childUnit;
                period.WorkTemplateAssigneeId = binding.Id; period.PeriodInstanceKey = binding.Id + ":ONCE";
                var report = Clone(fixture.Source); report.Id = Id(); report.WorkAssignmentId = assignment.Id;
                report.AssigneeUserId = childUser; report.CreatedByUserId = childUser;
                report.WorkReportPeriodId = period.Id; report.PeriodInstanceKey = period.PeriodInstanceKey;
                report.PayloadRevision = 0; report.PayloadHash = null; period.CurrentReportId = report.Id;
                var saved = await payload.SaveReportPayloadAsync(report, "[]", "{\"n\":30}", null, null, fixture.Actor, DateTime.UtcNow, ct);
                report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash;
                report.PayloadStatus = saved.PayloadStatus; report.PayloadSizeBytes = saved.PayloadSizeBytes;
                await db.WorkAssignments.InsertOneAsync(assignment, cancellationToken: ct);
                await db.WorkTemplateAssignees.InsertOneAsync(binding, cancellationToken: ct);
                await db.WorkReportPeriods.InsertOneAsync(period, cancellationToken: ct);
                await db.WorkAssignmentReports.InsertOneAsync(report, cancellationToken: ct);
                await store.ExecuteAsync(async (tx, cancel) => {
                    var window = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run, 1);
                    foreach (var item in new[] { ("REPORT", report.Id), ("SLOT", period.PeriodInstanceKey) })
                        await tx.PutAsync(AggregateCollections.Declarations, item.Item1 + ":" + item.Item2, 0,
                            new AggregateDeclarationState(item.Item1, item.Item2, fixture.WorkId, assignment.Id, window), fixture.WorkId, item.Item2, [], cancel);
                    return true;
                }, ct);
                count++;
            }
            Console.WriteLine($"MEASURE seeded={size} elapsedMs={seeded.ElapsedMilliseconds} work={fixture.WorkId}");
            var bootstrap = await Send("editor/bootstrap", new { reportId = fixture.Report.Id });
            var expected = bootstrap.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
            var request = new AggregatePreviewRequestDto(context, run + "-draft", "", expected, recipe, new([new("s", "FORM_SELECTOR", [], [])]));
            var before = await Snapshot();
            var watch = Stopwatch.StartNew();
            var page = await Send("sources/query", new AggregateDraftSourceQueryDto(context, recipe.Nodes[0].Form!, null, 10), true);
            Console.WriteLine($"MEASURE picker sources={size} pageSize=10 elapsedMs={watch.ElapsedMilliseconds} status={(page.TryGetProperty("code", out var error) ? error.GetString() : "OK")}");
            if (!page.TryGetProperty("code", out _))
                Check(page.GetProperty("items").GetArrayLength() == 10 && page.GetProperty("nextCursor").ValueKind == JsonValueKind.String,
                    $"{size} source picker limits view to 10 with continuation");
            watch.Restart();
            var result = await Send($"instances/{request.InstanceId}/preview", request, true);
            Console.WriteLine($"MEASURE preview sources={size} elapsedMs={watch.ElapsedMilliseconds} status={(result.TryGetProperty("code", out error) ? error.GetString() : "OK")}");
            if (!result.TryGetProperty("code", out _))
            {
                var preview = result.GetProperty("preview");
                Check(preview.GetProperty("results")[0].GetProperty("value").GetString() == (size * 30).ToString()
                    && preview.GetProperty("linkedSources").GetArrayLength() == size, $"{size} source calculation includes all sources beyond the picker page");
            }
            else Check(!result.TryGetProperty("preview", out _) && !result.TryGetProperty("token", out _),
                $"{size} source budget exhaustion returns no partial result or commit token");
            if (size >= 166)
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AggregateMapping:V2Enabled"] = "true" }).Build();
                var jobs = new AggregatePreviewJobs(db, payload, configuration);
                watch.Restart(); var jobId = await jobs.Start(new("RECIPE", context, request, null, null), fixture.Actor, run, ct);
                var startMs = watch.ElapsedMilliseconds; watch.Restart();
                var worker = jobs.Run(jobId, new DynamicFlowDefinitionTransactionRunner(db, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance), payload, ct);
                var seen = new List<int>();
                while (!worker.IsCompleted)
                {
                    var state = JsonSerializer.SerializeToElement(await jobs.Read(jobId, fixture.Actor, run, ct), AggregateCanonical.Json);
                    if (state.GetProperty("progress").ValueKind == JsonValueKind.Object) seen.Add(state.GetProperty("progress").GetProperty("processed").GetInt32());
                    await Task.WhenAny(worker, Task.Delay(1500, ct));
                }
                await worker;
                var finished = JsonSerializer.SerializeToElement(await jobs.Read(jobId, fixture.Actor, run, ct), AggregateCanonical.Json);
                Console.WriteLine($"MEASURE background-preview sources={size} startMs={startMs} runAndReadMs={watch.ElapsedMilliseconds} observedProcessed={string.Join(',', seen)} status={finished.GetProperty("state").GetString()}");
                Check(finished.GetProperty("state").GetString() == "COMPLETED"
                    && finished.GetProperty("result").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == (size * 30).ToString(),
                    $"{size} background preview completes the full source set with exact result");
            }
            Check(before == await Snapshot(), $"{size} source picker and preview leave aggregate/report snapshot unchanged");
        }
        Console.WriteLine("PASS exploratory load complete; no throughput/P95 acceptance claim; fixtures retained.");
    }
}
