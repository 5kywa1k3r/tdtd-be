using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Cache;
using tdtd_be.Common.Middleware;
using tdtd_be.Controllers;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using MongoDB.Bson;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.NativeStatisticTests;

// Only loopback Kestrel + the caller-owned Mongo. No application Program, Redis,
// recurring jobs, live catalog or published-owner mutation is started here.
internal static class NativeHttpCases
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task Run(Func<string, Func<Task>, Task> test, string connection, string database, string evidence)
    {
        var basic = await BasicResultCases.Fixture.Create(connection, database + "_http_basic", "PERIOD_RANGE", "2026-09-17", "2026-09-18");
        var advanced = await AdvancedResultCases.Fixture.Create(connection, database + "_http_advanced");
        // Duplicate a numeric value across different source days: DISTINCT must
        // operate on raw values, not add two daily distinct counts.
        var second = advanced.Inner.Reports[1];
        var secondPayload = await advanced.Inner.Payload.LoadReportPayloadAsync(second);
        var secondValues = JsonNode.Parse(secondPayload.TableValuesJson!)!;
        var matrix = secondValues["nativeTables"]!["tables"]!.AsArray().Single(t => t!["tableId"]!.GetValue<string>() == "a")!;
        matrix["rows"]!.AsArray().Single(r => r!["rowId"]!.GetValue<string>() == "a-r1")!["cells"]!["a-amount"]!["value"] = 2;
        var savedSecond = await advanced.Inner.Payload.SaveReportPayloadAsync(second, "{}", "{\"root-count\":6}", secondValues.ToJsonString(), null, advanced.Inner.Actor, second.PayloadUpdatedAtUtc!.Value);
        second.PayloadRevision = savedSecond.PayloadRevision; second.PayloadHash = savedSecond.PayloadHash;
        second.PayloadSizeBytes = savedSecond.PayloadSizeBytes; second.PayloadStatus = savedSecond.PayloadStatus;
        await advanced.Inner.Ctx.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == second.Id, second);
        var direct = await PublicationCases.Fixture.Create(connection, database + "_http_direct");
        await direct.Project();
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        var cache = new InMemoryUserSessionCache();
        var addresses = new List<string>();
        string Token(string? actor = null, bool expired = false, bool invalid = false, long version = 0)
        {
            var jwt = new JwtSecurityToken("canvas-isolated", "canvas-isolated",
                [new Claim("sub", actor ?? basic.Inner.Actor), new Claim("tv", version.ToString())],
                DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(expired ? -2 : 10),
                new SigningCredentials(invalid ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)) : key, SecurityAlgorithms.HmacSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
        async Task<WebApplication> Start(bool workers)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Testing", ContentRootPath = evidence });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(evidence, "isolated-keys")))
                .UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(basic.Inner.Ctx);
            builder.Services.AddSingleton<IUserSessionCache>(cache);
            builder.Services.AddScoped<MeContextRedisMiddleware>();
            builder.Services.AddScoped<ApiExceptionMiddleware>();
            builder.Services.AddSingleton<IStatRunCandidateActivation>(basic.Inner.Activation);
            builder.Services.AddScoped<IWorkAssignmentBasicSummaryService>(sp => basic.Service(
                backgroundJobs: sp.GetRequiredService<IBackgroundJobClient>(), httpAccessor: sp.GetRequiredService<IHttpContextAccessor>()));
            builder.Services.AddScoped<IWorkAssignmentAdvancedSummaryConfigService>(sp => advanced.Service(
                jobs: sp.GetRequiredService<IBackgroundJobClient>(), httpAccessor: sp.GetRequiredService<IHttpContextAccessor>()));
            // Constructor dependency for scalar actions; native actions never call it.
            builder.Services.AddScoped<IWorkAssignmentAdvancedSummaryHierarchyService>(sp => new WorkAssignmentAdvancedSummaryHierarchyService(
                advanced.Inner.Ctx, sp.GetRequiredService<IBackgroundJobClient>(), advanced.Inner.Payload, null!, advanced.Inner.Activation, null!));
            builder.Services.AddScoped(sp => new P9DirectResultService(direct.Ctx,
                new MeAccessor(sp.GetRequiredService<IHttpContextAccessor>()), direct.Service()));
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = "canvas-isolated", ValidateAudience = true,
                    ValidAudience = "canvas-isolated", ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                    ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(10) };
            });
            builder.Services.AddAuthorization();
            builder.Services.AddControllers().AddApplicationPart(typeof(WorkAssignmentBasicSummaryController).Assembly)
                .ConfigureApplicationPartManager(parts => parts.FeatureProviders.Add(new OnlyNativeControllers()));
            builder.Services.AddHangfire(c => c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
                .UseMongoStorage(new MongoClient(connection), database + "_http_jobs", new MongoStorageOptions {
                    Prefix = "canvas_native_http", CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.Poll,
                    QueuePollInterval = TimeSpan.FromMilliseconds(200),
                    MigrationOptions = new MongoMigrationOptions { MigrationStrategy = new MigrateMongoMigrationStrategy(), BackupStrategy = new CollectionMongoBackupStrategy() }
                }));
            if (workers) builder.Services.AddHangfireServer(o => { o.ServerName = "canvas-isolated-" + Guid.NewGuid().ToString("N"); o.WorkerCount = 1;
                o.Queues = ["default"]; o.SchedulePollingInterval = TimeSpan.FromSeconds(1); o.ShutdownTimeout = TimeSpan.FromSeconds(10); });
            var host = builder.Build();
            host.UseAuthentication(); host.UseMiddleware<MeContextRedisMiddleware>();
            host.UseMiddleware<ApiExceptionMiddleware>(); host.UseAuthorization(); host.MapControllers();
            try { await host.StartAsync(); addresses.Add(host.Urls.Single()); return host; }
            catch { await host.DisposeAsync(); throw; }
        }
        WebApplication? app = null;
        HttpClient? client = null;
        async Task Connect(bool workers = true)
        {
            app = await Start(workers); client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
        }
        async Task Disconnect()
        {
            client?.Dispose(); client = null;
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
        }
        async Task<(HttpStatusCode Status, string Raw)> Send(string path, object? body, string? token, bool get = false)
        {
            using var request = new HttpRequestMessage(get ? HttpMethod.Get : HttpMethod.Post, "/api/" + path);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!get) request.Content = new StringContent(body is string raw ? raw : JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var response = await client!.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        var basicPath = "work-assignment-basic-summary";
        var advancedPath = "work-assignment-advanced-summary";
        var directPath = "work-report-native-statistics/result";
        var pins = new Dictionary<string, JsonElement>();
        try
        {
            await Connect();
            foreach (var badRange in new[] { "2026-09-18..2026-09-17", "2026-02-29..2026-03-01", "2024-01-01..2025-01-01" })
                await test("HTTP RANGE rejects " + badRange, async () => {
                    var response = await Send(advancedPath + "/native-summary", advanced.Request("RANGE") with { GrainKey = badRange }, Token());
                    Check(response.Status == HttpStatusCode.BadRequest && response.Raw.Contains("ADVANCED_NATIVE_WINDOW_INVALID"), response.Raw);
                    Check(await advanced.Rows.CountDocumentsAsync(_ => true) == 0, "invalid range wrote snapshot");
                });
            foreach (var mode in new[] { "missing", "expired", "signature" })
                await test("HTTP JWT rejects " + mode + " on native routes", async () => {
                    foreach (var path in new[] { basicPath + "/native-summary", advancedPath + "/native-summary", directPath }) {
                        var response = await Send(path, basic.Request(), mode == "missing" ? null : Token(expired: mode == "expired", invalid: mode == "signature"));
                        Check(response.Status == HttpStatusCode.Unauthorized, "JWT accepted: " + response.Status);
                    }
                    Check(await basic.Rows.CountDocumentsAsync(_ => true) == 0, "unauthorized request wrote a snapshot");
                });
            await test("HTTP Me middleware rejects revoked token and disabled account", async () => {
                await cache.BumpTokenVersionAsync(basic.Inner.Actor);
                Check((await Send(basicPath + "/native-summary", basic.Request(), Token())).Status == HttpStatusCode.Unauthorized, "revoked token accepted");
                await basic.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == basic.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, true));
                await cache.DeleteMeAsync(basic.Inner.Actor);
                Check((await Send(basicPath + "/native-summary", basic.Request(), Token(version: 1))).Status == HttpStatusCode.Forbidden, "disabled user accepted");
                await basic.Inner.Ctx.Users.UpdateOneAsync(u => u.Id == basic.Inner.Actor, Builders<AppUser>.Update.Set(u => u.IsDeleted, false));
                await cache.DeleteMeAsync(basic.Inner.Actor);
            });
            foreach (var target in new[] { "basic", "advanced", "direct" })
                foreach (var fault in new[] { "duplicate", "unknown", "case-alias", "malformed", "missing", "null", "array", "wrong-type", "null-id" })
                    await test("HTTP " + target + " rejects " + fault + " before work", async () => {
                        var body = JsonSerializer.Serialize(target == "basic" ? (object)basic.Request() : target == "advanced" ? advanced.Request() : direct.Request(), Json);
                        var keyName = target == "direct" ? "workId" : "scopeAssignmentId";
                        var original = JsonDocument.Parse(body).RootElement.GetProperty(keyName).GetString();
                        var bad = fault == "missing" ? "{}" : fault == "null" ? "null" : fault == "array" ? "[]"
                            : fault is "wrong-type" or "null-id" ? body.Replace(JsonSerializer.Serialize(original), fault == "null-id" ? "null" : "123", StringComparison.Ordinal)
                            : fault == "malformed" ? "{broken" : "{" + (fault == "unknown" ? "\"surprise\":true," :
                            JsonSerializer.Serialize(fault == "case-alias" ? char.ToUpperInvariant(keyName[0]) + keyName[1..] : keyName) + ":" + JsonSerializer.Serialize(original) + ",") + body[1..];
                        var path = target == "basic" ? basicPath + "/native-refresh" : target == "advanced" ? advancedPath + "/native-refresh" : directPath;
                        var response = await Send(path, bad, Token(version: 1));
                        // Basic deliberately hides missing/invalid scope behind its
                        // existing ACL denial; do not weaken that privacy contract.
                        var expected = target == "basic" && fault is "missing" or "null-id" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest;
                        Check(response.Status == expected, "wrong rejection: " + response.Status + " " + response.Raw);
                    });
            foreach (var kind in new[] { "basic", "advanced", "range" })
                await test("HTTP " + kind + " authenticated enqueue hosted worker exact readback and replay", async () => {
                    var path = kind == "basic" ? basicPath : advancedPath;
                    object request = kind == "basic" ? basic.Request() with { CommandId = "http-basic-1" } : advanced.Request(kind == "range" ? "RANGE" : "DAY") with { CommandId = "http-" + kind + "-1" };
                    var queued = await Send(path + "/native-refresh", request, Token(version: 1));
                    Check(queued.Status == HttpStatusCode.OK, queued.Raw);
                    var intent = JsonDocument.Parse(queued.Raw).RootElement.Clone();
                    var id = intent.GetProperty("refreshId").GetString();
                    var jobId = intent.GetProperty("backgroundJobId").GetString();
                    Check(!string.IsNullOrWhiteSpace(jobId), "no real Hangfire job");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    JsonElement completed;
                    while (true) {
                        var status = await Send(path + "/native-refresh/" + id, null, Token(version: 1), true);
                        Check(status.Status == HttpStatusCode.OK, status.Raw);
                        completed = JsonDocument.Parse(status.Raw).RootElement.Clone();
                        if (completed.GetProperty("state").GetString() == "COMPLETED") break;
                        Check(completed.GetProperty("state").GetString() != "FAILED", status.Raw);
                        await Task.Delay(100, timeout.Token);
                    }
                    Check(completed.GetProperty("attempt").GetInt32() == 1, "duplicate attempt");
                    var snapshot = completed.GetProperty("snapshotId").GetString();
                    object read = kind == "basic" ? basic.Request(snapshot) : advanced.Request(kind == "range" ? "RANGE" : "DAY") with { SnapshotId = snapshot };
                    var result = await Send(path + "/native-summary", read, Token(version: 1));
                    Check(result.Status == HttpStatusCode.OK, result.Raw);
                    var value = JsonDocument.Parse(result.Raw).RootElement;
                    Check(value.GetProperty("snapshotHash").GetString() == completed.GetProperty("snapshotHash").GetString(), "different capture");
                    Check(value.GetProperty("nativeMetadata").GetProperty("version").GetInt32() == 1, "metadata missing");
                    var replay = await Send(path + "/native-refresh", request, Token(version: 1));
                    Check(replay.Status == HttpStatusCode.OK && JsonDocument.Parse(replay.Raw).RootElement.GetProperty("refreshId").GetString() == id, "command replay changed owner");
                    pins[kind] = completed;
                    await File.WriteAllTextAsync(Path.Combine(evidence, "http-" + kind + ".json"), result.Raw);
                    if (kind == "range") {
                        var groups = value.GetProperty("native").GetProperty("groups").EnumerateArray().ToArray();
                        var numeric = groups.Single(g => g.GetProperty("address").GetProperty("targetId").GetString() == "a-legacy"
                            && g.GetProperty("address").GetProperty("rowId").GetString() == "a-r1").GetProperty("operations").EnumerateArray().ToArray();
                        foreach (var (method, expected) in new[] { ("SUM", "4"), ("COUNT", "2"), ("DISTINCT_COUNT", "1") })
                            Check(numeric.Single(o => o.GetProperty("method").GetString() == method).GetProperty("value").GetProperty("text").GetString() == expected, "range " + method);
                        var avg = numeric.Single(o => o.GetProperty("method").GetString() == "AVG").GetProperty("numeric");
                        Check(avg.GetProperty("sum").GetString() == "4" && avg.GetProperty("count").GetInt64() == 2, "range AVG");
                        var text = groups.Single(g => g.GetProperty("address").GetProperty("targetId").GetString() == "a-text-plan")
                            .GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("operationId").GetString() == "a-concat-0");
                        Check(string.Concat(text.GetProperty("textChunks").EnumerateArray().Select(c => c.GetProperty("text").GetString())) ==
                            "4\nViệt 1\r\n🌿\nfalse\n2\nViệt 1\r\n🌿\nfalse\n8\nViệt 2\r\n🌿\nfalse\n2\nViệt 2\r\n🌿\nfalse", "range CONCAT ordering/type/newlines");
                        foreach (var operation in groups.Single(g => g.GetProperty("address").GetProperty("targetId").GetString() == "a-rows").GetProperty("operations").EnumerateArray()) {
                            var stack = operation.GetProperty("stack"); var rows = operation.GetProperty("method").GetString() == "STACK_ROWS";
                            Check(stack.GetProperty("rows").GetInt32() == (rows ? 4 : 2) && stack.GetProperty("columns").GetInt32() == (rows ? 3 : 6), "range STACK geometry");
                            var blocks = stack.GetProperty("blocks").EnumerateArray().ToArray();
                            Check(blocks.Length == 2 && blocks[1].GetProperty("reportId").GetString() == second.Id
                                && blocks[1].GetProperty(rows ? "rowOffset" : "columnOffset").GetInt32() == (rows ? 2 : 3), "range STACK provenance");
                        }
                        var history = await Send(path + "/native-summary", advanced.Request("RANGE") with { SnapshotId = snapshot, Historical = true }, Token(version: 1));
                        Check(history.Status == HttpStatusCode.OK && JsonDocument.Parse(history.Raw).RootElement.GetProperty("snapshotHash").GetString() == value.GetProperty("snapshotHash").GetString(), "range exact history");
                        await File.WriteAllTextAsync(Path.Combine(evidence, "http-range-history.json"), history.Raw);
                    }
                });
            await test("HTTP Direct current and exact history preserve publication metadata", async () => {
                var job = await direct.Job();
                foreach (var history in new[] { false, true }) {
                    var response = await Send(directPath, direct.Request() with { Historical = history, RunId = job.Id, GenerationId = job.GenerationId }, Token(version: 1));
                    Check(response.Status == HttpStatusCode.OK, response.Raw);
                    var value = JsonDocument.Parse(response.Raw).RootElement;
                    Check(value.GetProperty("metadata").GetProperty("freshness").GetString() == (history ? "HISTORICAL" : "FRESH"), "wrong freshness");
                    Check(value.GetProperty("nativeMetadata").GetProperty("version").GetInt32() == 1, "metadata lost");
                    await File.WriteAllTextAsync(Path.Combine(evidence, history ? "http-direct-history.json" : "http-direct-current.json"), response.Raw);
                }
            });
            await test("HTTP valid different actor cannot read results or recover commands", async () => {
                var other = "cccccccccccccccccccccccc";
                await basic.Inner.Ctx.Users.InsertOneAsync(new AppUser { Id = other, Username = "isolated-other", FullName = "Other", PasswordHash = "fixture" });
                foreach (var path in new[] { basicPath + "/native-refresh/by-command/http-basic-1", advancedPath + "/native-refresh/by-command/http-advanced-1" }) {
                    var response = await Send(path, null, Token(other), true);
                    Check((int)response.Status is >= 400 and < 500 && !response.Raw.Contains("snapshotHash\":\""), "intent leaked");
                }
                foreach (var item in new[] { (basicPath + "/native-summary", (object)basic.Request()), (advancedPath + "/native-summary", (object)advanced.Request()), (directPath, (object)direct.Request()) }) {
                    var response = await Send(item.Item1, item.Item2, Token(other));
                    Check((int)response.Status is >= 400 and < 500 && !response.Raw.Contains("nativeMetadata"), "result leaked");
                }
            });
            await test("HTTP host restart recovers both completed commands from Mongo without recomputing", async () => {
                await Disconnect(); await Connect();
                foreach (var kind in new[] { "basic", "advanced", "range" }) {
                    var response = await Send((kind == "basic" ? basicPath : advancedPath) + "/native-refresh/by-command/http-" + kind + "-1", null, Token(version: 1), true);
                    Check(response.Status == HttpStatusCode.OK, response.Raw);
                    var value = JsonDocument.Parse(response.Raw).RootElement;
                    Check(value.GetProperty("snapshotHash").GetString() == pins[kind].GetProperty("snapshotHash").GetString()
                        && value.GetProperty("attempt").GetInt32() == 1, "restart recalculated");
                }
            });
            await test("HTTP durable Hangfire queue survives host stop before worker starts", async () => {
                await Disconnect(); await Connect(workers: false);
                var queued = new Dictionary<string, string>();
                foreach (var kind in new[] { "basic", "advanced", "range" }) {
                    object request = kind == "basic" ? basic.Request() with { CommandId = "restart-basic" } : advanced.Request(kind == "range" ? "RANGE" : "DAY") with { CommandId = "restart-" + kind };
                    var response = await Send((kind == "basic" ? basicPath : advancedPath) + "/native-refresh", request, Token(version: 1));
                    Check(response.Status == HttpStatusCode.OK, response.Raw);
                    var value = JsonDocument.Parse(response.Raw).RootElement;
                    Check(value.GetProperty("state").GetString() == "QUEUED" && value.GetProperty("attempt").GetInt32() == 0, "worker unexpectedly ran");
                    queued[kind] = value.GetProperty("refreshId").GetString()!;
                }
                await Disconnect(); await Connect();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                foreach (var kind in new[] { "basic", "advanced", "range" }) {
                    while (true) {
                        var response = await Send((kind == "basic" ? basicPath : advancedPath) + "/native-refresh/" + queued[kind], null, Token(version: 1), true);
                        Check(response.Status == HttpStatusCode.OK, response.Raw);
                        var value = JsonDocument.Parse(response.Raw).RootElement;
                        if (value.GetProperty("state").GetString() == "COMPLETED") {
                            Check(value.GetProperty("attempt").GetInt32() == 1 && value.GetProperty("snapshotHash").GetString() == pins[kind].GetProperty("snapshotHash").GetString(), "queue rebased/recomputed differently");
                            break;
                        }
                        Check(value.GetProperty("state").GetString() != "FAILED", response.Raw);
                        await Task.Delay(100, timeout.Token);
                    }
                }
            });
            var rangePin = pins["range"];
            var compareRequest = new AdvancedNativeComparisonRequest(advanced.Root, advanced.Inner.Template.Id, "part-main",
                "RANGE", "2026-09-17..2026-09-18", rangePin.GetProperty("snapshotId").GetString()!, rangePin.GetProperty("snapshotHash").GetString()!);
            var comparePath = advancedPath + "/native-comparison";
            string? comparisonRecordId = null, comparisonRecordHash = null;
            var recordsPath = advancedPath + "/native-comparison-records";
            await advanced.Inner.Ctx.Users.InsertOneAsync(new AppUser { Id = "cccccccccccccccccccccccc", Username = "isolated-other", FullName = "Other", PasswordHash = "fixture" });
            foreach (var mode in new[] { "missing", "expired", "signature", "other" })
                await test("HTTP comparison rejects actor " + mode, async () => {
                    var response = await Send(comparePath, compareRequest, mode == "missing" ? null : mode == "other" ? Token("cccccccccccccccccccccccc") : Token(version: 1, expired: mode == "expired", invalid: mode == "signature"));
                    Check(response.Status == (mode == "other" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized), response.Raw);
                    Check(!response.Raw.Contains("\"verdict\""), "unauthorized verdict leaked");
                });
            foreach (var extra in new[] { "\"historical\":true,", "\"surprise\":1,", "\"snapshotHash\":\"bad\"," })
                await test("HTTP comparison strict request " + extra, async () => {
                    var body = JsonSerializer.Serialize(compareRequest, Json);
                    var response = await Send(comparePath, "{" + extra + body[1..], Token(version: 1));
                    Check(response.Status == HttpStatusCode.BadRequest, response.Raw);
                });
            await test("HTTP comparison busy budget refuses without queue", async () => {
                using var first = AdvancedNativeComparisonBudget.Enter(); using var secondSlot = AdvancedNativeComparisonBudget.Enter();
                var response = await Send(comparePath, compareRequest, Token(version: 1));
                Check((int)response.Status == 429 && response.Raw.Contains("ADVANCED_NATIVE_COMPARISON_BUSY"), response.Raw);
            });
            await test("HTTP comparison returns pinned MATCH and releases budget", async () => {
                var response = await Send(comparePath, compareRequest, Token(version: 1));
                Check(response.Status == HttpStatusCode.OK, response.Raw);
                var verdict = JsonDocument.Parse(response.Raw).RootElement;
                Check(verdict.GetProperty("verdict").GetString() == "MATCH" && verdict.GetProperty("coverage").GetString() == "ADVANCED_NATIVE_CURRENT"
                    && verdict.GetProperty("snapshotHash").GetString() == compareRequest.SnapshotHash && verdict.GetProperty("differences").GetArrayLength() == 0, response.Raw);
                await File.WriteAllTextAsync(Path.Combine(evidence, "http-comparison-match.json"), response.Raw);
            });
            await test("comparison client cancellation releases execution slots", async () => {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await advanced.Service().CompareNativeSummaryAsync(compareRequest, cancelled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { }
                using var one = AdvancedNativeComparisonBudget.Enter(); using var two = AdvancedNativeComparisonBudget.Enter();
            });
            await test("HTTP comparison wrong artifact pin returns error not MATCH", async () => {
                var response = await Send(comparePath, compareRequest with { SnapshotHash = new string('0', 64) }, Token(version: 1));
                Check(response.Status == HttpStatusCode.BadRequest && response.Raw.Contains("ADVANCED_NATIVE_COMPARISON_ARTIFACT_CHANGED"), response.Raw);
            });
            await test("HTTP comparison record recomputes and persists exact observation", async () => {
                var response = await Send(recordsPath, compareRequest, Token(version: 1));
                Check(response.Status == HttpStatusCode.OK, response.Raw);
                var record = JsonDocument.Parse(response.Raw).RootElement;
                comparisonRecordId = record.GetProperty("recordId").GetString(); comparisonRecordHash = record.GetProperty("recordHash").GetString();
                Check(record.GetProperty("freshness").GetString() == "HISTORICAL_OBSERVATION"
                    && record.GetProperty("actorId").GetString() == advanced.Inner.Actor
                    && record.GetProperty("observation").GetProperty("verdict").GetString() == "MATCH", response.Raw);
                var row = await advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName)
                    .Find(new BsonDocument("_id", comparisonRecordId)).SingleAsync();
                var stored = JsonDocument.Parse(row["json"].AsString).RootElement;
                Check(stored.GetProperty("expectedResultHash").GetString() == stored.GetProperty("actualResultHash").GetString()
                    && stored.GetProperty("expectedIdentity").GetProperty("configuration").GetProperty("configHash").GetString() != null
                    && stored.GetProperty("assignmentIds").GetArrayLength() == 2
                    && stored.GetProperty("expectedEvidence").GetProperty("sources").GetArrayLength() == 2
                    && stored.GetProperty("actualEvidence").GetProperty("schemaHash").GetString() == stored.GetProperty("expectedEvidence").GetProperty("schemaHash").GetString(), "record evidence missing");
                await File.WriteAllTextAsync(Path.Combine(evidence, "http-record-created.json"), response.Raw);
            });
            await test("comparison record transaction replay is immutable", async () => {
                var rows = advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName);
                var row = await rows.Find(new BsonDocument("_id", comparisonRecordId)).SingleAsync();
                var record = JsonSerializer.Deserialize<AdvancedNativeComparisonRecord>(row["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
                var store = new AdvancedNativeComparisonRecordStore(advanced.Inner.Ctx.Db);
                using var session = await advanced.Inner.Ctx.Db.Client.StartSessionAsync();
                session.StartTransaction();
                Check(await store.AppendAsync(session, record, default) == comparisonRecordHash, "replay hash changed");
                await session.CommitTransactionAsync();
                session.StartTransaction();
                try { await store.AppendAsync(session, record with { ExpectedResultHash = new string('0',64) }, default); throw new Exception("Conflicting record overwritten"); }
                catch (tdtd_be.Common.Errors.AppException) { }
                finally { await session.AbortTransactionAsync(); }
                Check((await rows.Find(new BsonDocument("_id", comparisonRecordId)).SingleAsync()).Equals(row), "record changed");
            });
            await test("comparison reference is atomically bound to exact stored evidence", async () => {
                var registry = advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName);
                var reference = await registry.Find(new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + comparisonRecordId)).SingleAsync();
                var source = await advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName)
                    .Find(new BsonDocument("_id", comparisonRecordId)).SingleAsync();
                var record = JsonSerializer.Deserialize<AdvancedNativeComparisonRecord>(source["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
                Check(reference["sourceContentSha256"] == comparisonRecordHash && reference["snapshotId"] == record.Observation.SnapshotId
                    && reference["dynamicFormTemplateId"] == record.Observation.DynamicFormTemplateId && reference["actorId"] == record.ActorId
                    && reference["refreshId"].IsBsonNull && reference["grainKey"] == record.Observation.GrainKey, "incorrect comparison binding");
                using var session = await advanced.Inner.Ctx.Db.Client.StartSessionAsync(); session.StartTransaction();
                await new AdvancedNativeComparisonRecordStore(advanced.Inner.Ctx.Db).AppendAsync(session, record, default);
                await session.CommitTransactionAsync();
                Check((await registry.Find(new BsonDocument("_id", reference["_id"])).SingleAsync()).Equals(reference), "reference replay changed");
            });
            foreach (var conflict in new[] { false, true }) await test("comparison reference rollback conflict=" + conflict, async () => {
                var db = advanced.Inner.Ctx.Db;
                var rows = db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName);
                var registry = db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName);
                var source = await rows.Find(new BsonDocument("_id", comparisonRecordId)).SingleAsync();
                var record = JsonSerializer.Deserialize<AdvancedNativeComparisonRecord>(source["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
                record = record with { Id = ObjectId.GenerateNewId().ToString() };
                var key = new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + record.Id);
                var damaged = new BsonDocument { { "_id", key["_id"] }, { "raw", "retain" } };
                if (conflict) await registry.InsertOneAsync(damaged);
                using var session = await db.Client.StartSessionAsync(); session.StartTransaction();
                try {
                    await new AdvancedNativeComparisonRecordStore(db).AppendAsync(session, record, default);
                    Check(!conflict, "registry conflict accepted");
                } catch (tdtd_be.Common.Errors.AppException) { Check(conflict, "unexpected rejection"); }
                finally { await session.AbortTransactionAsync(); }
                Check(await rows.CountDocumentsAsync(new BsonDocument("_id", record.Id)) == 0, "source escaped rollback");
                var reference = await registry.Find(key).SingleOrDefaultAsync();
                Check(conflict ? reference.Equals(damaged) : reference is null, "reference escaped rollback or raw overwritten");
                if (conflict) await registry.DeleteOneAsync(key); // remove only this isolated injected fixture
            });
            await test("comparison read does not backfill missing legacy reference", async () => {
                var db = advanced.Inner.Ctx.Db;
                var registry = db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName);
                var key = new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + comparisonRecordId);
                var original = await registry.Find(key).SingleAsync(); await registry.DeleteOneAsync(key);
                try {
                    Check(await new AdvancedNativeComparisonRecordStore(db).ReadAsync(comparisonRecordId!, comparisonRecordHash!, default) is not null, "legacy read failed");
                    Check(await registry.CountDocumentsAsync(key) == 0, "getter backfilled registry");
                } finally { await registry.InsertOneAsync(original); }
            });
            await test("HTTP record diagnostic verifies exact reference with bounded coverage", async () => {
                var response = await Send(recordsPath + "/" + comparisonRecordId + "/diagnostic?hash=" + comparisonRecordHash, null, Token(version: 1), true);
                Check(response.Status == HttpStatusCode.OK, response.Raw);
                var d = JsonDocument.Parse(response.Raw).RootElement;
                Check(d.GetProperty("referenceStatus").GetString() == "VERIFIED" && d.GetProperty("coverage").GetString() == "SELECTED_RECORD_ONLY"
                    && d.GetProperty("consistency").GetString() == "SNAPSHOT" && !d.GetProperty("canDelete").GetBoolean() && !d.GetProperty("canRewrite").GetBoolean(), response.Raw);
            });
            foreach (var missing in new[] { true, false }) await test("HTTP record diagnostic missing/corrupt reference missing=" + missing, async () => {
                var registry = advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName);
                var key = new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + comparisonRecordId);
                var original = await registry.Find(key).SingleAsync();
                if (missing) await registry.DeleteOneAsync(key);
                else await registry.UpdateOneAsync(key, Builders<BsonDocument>.Update.Set("sourceContentSha256", new string('0', 64)));
                try {
                    var response = await Send(recordsPath + "/" + comparisonRecordId + "/diagnostic?hash=" + comparisonRecordHash, null, Token(version: 1), true);
                    if (missing) {
                        Check(response.Status == HttpStatusCode.OK && response.Raw.Contains("MISSING_COVERAGE_UNKNOWN"), response.Raw);
                        Check(await registry.CountDocumentsAsync(key) == 0, "diagnostic backfilled");
                    } else {
                        Check(response.Status == HttpStatusCode.BadRequest && response.Raw.Contains("NATIVE_STORAGE_REFERENCE_INTEGRITY"), response.Raw);
                        Check((await registry.Find(key).SingleAsync())["sourceContentSha256"] == new string('0', 64), "raw repaired");
                    }
                } finally { await registry.ReplaceOneAsync(key, original, new ReplaceOptions { IsUpsert = true }); }
            });
            await test("HTTP record diagnostic rejects another actor and wrong source hash", async () => {
                var path = recordsPath + "/" + comparisonRecordId + "/diagnostic?hash=";
                var actor = await Send(path + comparisonRecordHash, null, Token("cccccccccccccccccccccccc"), true);
                var hash = await Send(path + new string('0',64), null, Token(version: 1), true);
                Check(actor.Status != HttpStatusCode.OK && hash.Status != HttpStatusCode.OK && !actor.Raw.Contains("VERIFIED") && !hash.Raw.Contains("VERIFIED"), "diagnostic leaked");
            });
            var listPath = recordsPath + "?scopeId=" + compareRequest.ScopeAssignmentId + "&formId=" + compareRequest.DynamicFormTemplateId;
            await test("HTTP comparison list verifies source and exposes bounded coverage", async () => {
                var response = await Send(listPath, null, Token(version: 1), true);
                Check(response.Status == HttpStatusCode.OK, response.Raw);
                var page = JsonDocument.Parse(response.Raw).RootElement;
                Check(page.GetProperty("items").GetArrayLength() == 1 && page.GetProperty("items")[0].GetProperty("recordHash").GetString() == comparisonRecordHash
                    && page.GetProperty("coverage").GetString() == "ACTOR_INDEXED_RECORDS_ONLY_COVERAGE_UNKNOWN"
                    && !page.GetProperty("canDelete").GetBoolean(), response.Raw);
            });
            await test("HTTP comparison list paginates by exact source ID without duplicates", async () => {
                var db = advanced.Inner.Ctx.Db; var rows = db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName);
                var raw = await rows.Find(new BsonDocument("_id", comparisonRecordId)).SingleAsync();
                var record = JsonSerializer.Deserialize<AdvancedNativeComparisonRecord>(raw["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
                var clone = record with { Id = ObjectId.GenerateNewId().ToString() };
                using var session = await db.Client.StartSessionAsync(); session.StartTransaction();
                await new AdvancedNativeComparisonRecordStore(db).AppendAsync(session, clone, default); await session.CommitTransactionAsync();
                try {
                    var first = await Send(listPath + "&limit=1", null, Token(version: 1), true); Check(first.Status == HttpStatusCode.OK, first.Raw);
                    var p = JsonDocument.Parse(first.Raw).RootElement; var next = p.GetProperty("nextAfter").GetString();
                    Check(next == comparisonRecordId, first.Raw);
                    var second = await Send(listPath + "&limit=1&after=" + next, null, Token(version: 1), true); Check(second.Status == HttpStatusCode.OK, second.Raw);
                    var q = JsonDocument.Parse(second.Raw).RootElement;
                    Check(q.GetProperty("items")[0].GetProperty("recordId").GetString() == clone.Id && q.GetProperty("nextAfter").ValueKind == JsonValueKind.Null, second.Raw);
                } finally {
                    await rows.DeleteOneAsync(new BsonDocument("_id", clone.Id));
                    await db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName).DeleteOneAsync(new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + clone.Id));
                }
            });
            foreach (var query in new[] { "&limit=0", "&limit=21", "&after=bad" }) await test("HTTP comparison list rejects input " + query, async () => {
                Check((await Send(listPath + query, null, Token(version: 1), true)).Status == HttpStatusCode.BadRequest, "invalid list accepted");
            });
            await test("HTTP comparison list denies other actor before returning any entries", async () => {
                var response = await Send(listPath, null, Token("cccccccccccccccccccccccc"), true);
                Check(response.Status == HttpStatusCode.Forbidden && !response.Raw.Contains(comparisonRecordHash!), response.Raw);
            });
            await test("HTTP comparison list refuses locator alias pointing at a valid record", async () => {
                var registry = advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName);
                var row = await registry.Find(new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + comparisonRecordId)).SingleAsync();
                var alias = row.DeepClone().AsBsonDocument; alias["_id"] = "ADVANCED_COMPARISON_RECORD:" + ObjectId.GenerateNewId();
                await registry.InsertOneAsync(alias);
                try {
                    var response = await Send(listPath, null, Token(version: 1), true);
                    Check(response.Status == HttpStatusCode.BadRequest && !response.Raw.Contains("\"items\""), response.Raw);
                } finally { await registry.DeleteOneAsync(new BsonDocument("_id", alias["_id"])); }
            });
            foreach (var missing in new[] { true, false }) await test("HTTP comparison list missing/corrupt locator missing=" + missing, async () => {
                var registry = advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(tdtd_be.Services.WorkAssignments.NativeStorageReferenceStore.CollectionName);
                var key = new BsonDocument("_id", "ADVANCED_COMPARISON_RECORD:" + comparisonRecordId); var original = await registry.Find(key).SingleAsync();
                if (missing) await registry.DeleteOneAsync(key); else await registry.UpdateOneAsync(key, Builders<BsonDocument>.Update.Set("bindingSha256", "broken"));
                try {
                    var response = await Send(listPath, null, Token(version: 1), true);
                    if (missing) { Check(response.Status == HttpStatusCode.OK && response.Raw.Contains("COVERAGE_UNKNOWN") && JsonDocument.Parse(response.Raw).RootElement.GetProperty("items").GetArrayLength() == 0, response.Raw); }
                    else { Check(response.Status == HttpStatusCode.BadRequest && !response.Raw.Contains("\"items\""), response.Raw); }
                } finally { await registry.ReplaceOneAsync(key, original, new ReplaceOptions { IsUpsert = true }); }
            });
            await test("HTTP record survives host restart with exact hash and no recompute", async () => {
                await Disconnect(); await Connect();
                var response = await Send(recordsPath + "/" + comparisonRecordId + "?hash=" + comparisonRecordHash, null, Token(version: 1), true);
                Check(response.Status == HttpStatusCode.OK && response.Raw == await File.ReadAllTextAsync(Path.Combine(evidence, "http-record-created.json")), response.Raw);
            });
            await test("HTTP record rejects another actor and wrong hash", async () => {
                var other = await Send(recordsPath + "/" + comparisonRecordId + "?hash=" + comparisonRecordHash, null, Token("cccccccccccccccccccccccc"), true);
                Check((int)other.Status is >= 400 and < 500 && !other.Raw.Contains("\"observation\""), other.Raw);
                var wrong = await Send(recordsPath + "/" + comparisonRecordId + "?hash=" + new string('0',64), null, Token(version: 1), true);
                Check(wrong.Status == HttpStatusCode.BadRequest && !wrong.Raw.Contains("\"observation\""), wrong.Raw);
            });
            await test("HTTP record endpoint refuses a client supplied verdict", async () => {
                var json = JsonSerializer.Serialize(compareRequest, Json);
                var response = await Send(recordsPath, "{\"verdict\":\"MATCH\"," + json[1..], Token(version: 1));
                Check(response.Status == HttpStatusCode.BadRequest, response.Raw);
                Check(await advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName).CountDocumentsAsync(_ => true) == 1, "invalid request wrote record");
            });
            await test("HTTP comparison reports DIFFERENT without repairing stored actual", async () => {
                var row = await advanced.Rows.Find(new BsonDocument("_id", compareRequest.SnapshotId)).SingleAsync();
                var snapshot = JsonSerializer.Deserialize<AdvancedNativeSnapshot>(row["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
                var group = snapshot.Native.Groups.First(g => g.Operations.Any(o => o.Method == "SUM"));
                var operation = group.Operations.First(o => o.Method == "SUM");
                var changed = snapshot with { Native = snapshot.Native with { Groups = snapshot.Native.Groups.Select(g => g == group
                    ? g with { Operations = g.Operations.Select(o => o == operation ? o with { Value = o.Value! with { Text = "999" } } : o).ToArray() } : g).ToArray() } };
                var text = StatConfigCanonicalJson.Canonicalize(changed); var hash = StatRunCanonicalJson.HashText(text);
                await advanced.Rows.UpdateOneAsync(new BsonDocument("_id", compareRequest.SnapshotId), Builders<BsonDocument>.Update.Set("json", text).Set("hash", hash).Set("bytes", Encoding.UTF8.GetByteCount(text)));
                compareRequest = compareRequest with { SnapshotHash = hash };
                var response = await Send(comparePath, compareRequest, Token(version: 1));
                Check(response.Status == HttpStatusCode.OK, response.Raw);
                var verdict = JsonDocument.Parse(response.Raw).RootElement;
                Check(verdict.GetProperty("verdict").GetString() == "DIFFERENT" && verdict.GetProperty("differences").GetArrayLength() == 1, response.Raw);
                Check((await advanced.Rows.Find(new BsonDocument("_id", compareRequest.SnapshotId)).SingleAsync())["json"] == text, "actual was repaired");
                await File.WriteAllTextAsync(Path.Combine(evidence, "http-comparison-different.json"), response.Raw);
            });
            await test("HTTP comparison reports INCOMPARABLE for different capture pins", async () => {
                var row = await advanced.Rows.Find(new BsonDocument("_id", compareRequest.SnapshotId)).SingleAsync();
                var snapshot = JsonSerializer.Deserialize<AdvancedNativeSnapshot>(row["json"].AsString, StatConfigCanonicalJson.StrictJsonOptions)!;
                var text = StatConfigCanonicalJson.Canonicalize(snapshot with { SourceSetHash = new string('1', 64) });
                var hash = StatRunCanonicalJson.HashText(text);
                await advanced.Rows.UpdateOneAsync(new BsonDocument("_id", compareRequest.SnapshotId), Builders<BsonDocument>.Update.Set("json", text).Set("hash", hash).Set("bytes", Encoding.UTF8.GetByteCount(text)));
                compareRequest = compareRequest with { SnapshotHash = hash };
                var response = await Send(comparePath, compareRequest, Token(version: 1));
                Check(response.Status == HttpStatusCode.OK && JsonDocument.Parse(response.Raw).RootElement.GetProperty("verdict").GetString() == "INCOMPARABLE", response.Raw);
                await File.WriteAllTextAsync(Path.Combine(evidence, "http-comparison-incomparable.json"), response.Raw);
            });
            await test("HTTP comparison stale source refuses verdict", async () => {
                await advanced.Inner.Ctx.WorkAssignmentReports.UpdateOneAsync(r => r.Id == advanced.Inner.Reports[0].Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.CumulativeContributionMode, "EXCLUDE"));
                var response = await Send(comparePath, compareRequest, Token(version: 1));
                Check(response.Status == HttpStatusCode.BadRequest && response.Raw.Contains("ADVANCED_NATIVE_SNAPSHOT_STALE") && !response.Raw.Contains("\"verdict\""), response.Raw);
            });
            await test("HTTP historical observation remains unchanged after source and artifact drift", async () => {
                var response = await Send(recordsPath + "/" + comparisonRecordId + "?hash=" + comparisonRecordHash, null, Token(version: 1), true);
                Check(response.Status == HttpStatusCode.OK && response.Raw == await File.ReadAllTextAsync(Path.Combine(evidence, "http-record-created.json")), response.Raw);
                await File.WriteAllTextAsync(Path.Combine(evidence, "http-record-history.json"), response.Raw);
                var save = await Send(recordsPath, compareRequest, Token(version: 1));
                Check(save.Status == HttpStatusCode.BadRequest, save.Raw);
                Check(await advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName).CountDocumentsAsync(_ => true) == 1, "stale source wrote record");
            });
            await test("HTTP record rechecks access to every captured assignment", async () => {
                var assignmentId = advanced.Inner.Reports[1].WorkAssignmentId;
                var original = await advanced.Inner.Ctx.WorkAssignments.Find(a => a.Id == assignmentId).SingleAsync();
                var changed = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WorkAssignment>(original.ToBsonDocument());
                changed.CreatedByUserId = "cccccccccccccccccccccccc"; changed.Assignees = [];
                await advanced.Inner.Ctx.WorkAssignments.ReplaceOneAsync(a => a.Id == assignmentId, changed);
                try {
                    var response = await Send(recordsPath + "/" + comparisonRecordId + "?hash=" + comparisonRecordHash, null, Token(version: 1), true);
                    Check(response.Status == HttpStatusCode.Forbidden && !response.Raw.Contains("\"observation\""), response.Raw);
                }
                finally { await advanced.Inner.Ctx.WorkAssignments.ReplaceOneAsync(a => a.Id == assignmentId, original); }
            });
            await test("HTTP comparison requires scope management permission", async () => {
                await advanced.Inner.Ctx.WorkAssignments.UpdateOneAsync(a => a.Id == advanced.Root,
                    Builders<WorkAssignment>.Update.Set(a => a.CreatedByUserId, "cccccccccccccccccccccccc"));
                var response = await Send(comparePath, compareRequest, Token(version: 1));
                Check(response.Status == HttpStatusCode.Forbidden && response.Raw.Contains("ADVANCED_SUMMARY_CONFIG_MANAGE_FORBIDDEN"), response.Raw);
            });
            await test("HTTP historical record rechecks current management permission", async () => {
                var response = await Send(recordsPath + "/" + comparisonRecordId + "?hash=" + comparisonRecordHash, null, Token(version: 1), true);
                Check(response.Status == HttpStatusCode.Forbidden && !response.Raw.Contains("\"observation\""), response.Raw);
            });
            await test("record corruption is retained and explicitly rejected", async () => {
                var rows = advanced.Inner.Ctx.Db.GetCollection<BsonDocument>(AdvancedNativeComparisonRecordStore.CollectionName);
                await rows.UpdateOneAsync(new BsonDocument("_id", comparisonRecordId), Builders<BsonDocument>.Update.Set("bytes", 1));
                var before = (await rows.Find(_ => true).SingleAsync()).ToJson();
                try { await new AdvancedNativeComparisonRecordStore(advanced.Inner.Ctx.Db).ReadAsync(comparisonRecordId!, comparisonRecordHash!, default); throw new Exception("Corrupt record accepted"); }
                catch (tdtd_be.Common.Errors.AppException ex) { Check(JsonSerializer.Serialize(ex.Details).Contains("RECORD_INTEGRITY"), ex.Message); }
                Check((await rows.Find(_ => true).SingleAsync()).ToJson() == before, "corruption repaired");
            });
        }
        finally
        {
            await Disconnect();
            await File.WriteAllTextAsync(Path.Combine(evidence, "http-host-stopped.json"), JsonSerializer.Serialize(new { addresses, stopped = true, hostedWorker = true, syntheticPublishedOwner = true, activationFixture = true, sessionCache = "in-memory" }, Json));
        }
    }
    private sealed class OnlyNativeControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var allowed = new[] { typeof(WorkAssignmentBasicSummaryController), typeof(WorkAssignmentAdvancedSummaryController), typeof(WorkReportNativeStatisticController) };
            foreach (var type in feature.Controllers.Where(t => !allowed.Contains(t.AsType())).ToArray()) feature.Controllers.Remove(type);
        }
    }
}
