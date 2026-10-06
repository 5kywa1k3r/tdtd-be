// R7 own fixture; real Mongo/Hangfire/JWT checks, no reset or cleanup.
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

var browserHold = args.LastOrDefault() == "--browser-hold";
if (!(browserHold ? args.Take(args.Length - 1) : args).SequenceEqual(new[] { "--database", "tdtd", "--run-own-error-fixture" })) throw new ArgumentException("Use --database tdtd --run-own-error-fixture");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(browserHold ? 20 : 5)); var ct = timeout.Token;
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
var hello = await db.Db.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct);
if (hello.GetValue("setName", "") != "tdtd-rs" || !hello.GetValue("isWritablePrimary", false).AsBoolean) throw new Exception("Requires authorized local writable tdtd replica set.");
await FixtureProvenanceChecks.Migration(db, "R7 before fixture", ct);
var run = "r7-errors-20261005-" + Guid.NewGuid().ToString("N")[..8]; var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var table = new DynamicFormNativeTableDto { Id = "items", SectionId = "main", Name = "Danh sách — Dữ liệu thử riêng", Order = 0, Layout = "vertical",
    Fields = [new() { Id = "score", Name = "Số vụ", Order = 0 }], Rows = [], StatisticTargets = [],
    Presentation = new() { Kind = "LIST", ItemLabel = "Phần tử", AddLabel = "Thêm phần tử", SummaryFieldIds = ["score"] },
    ItemConstraints = new() { MinItems = 0, MaxItems = 200 }, TypeConfig = new() { Version = 1, Sequence = 1,
    Rules = [new() { Order = 1, Target = new() { Scope = "column", FieldId = "score" }, Spec = new() { Type = "number", Required = false } }] } };
var f = await RuntimeFixture.Seed(db, run, ct, configureForm: (form, target) => {
    if (!target) { form.NativeTablesVersion = 2; form.TablesJson = JsonSerializer.Serialize(new[] { table }, AggregateCanonical.Json); }
});
var payload = new WorkReportPayloadService(db);
var tables = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = f.Source.DynamicFormSchemaHash,
    tables = new[] { new { tableId = "items", records = new[] {
        new { recordId = "00000000-0000-4000-8000-000000000001", cells = new { score = new { type = "number", state = "value", value = 12 } } },
        new { recordId = "00000000-0000-4000-8000-000000000002", cells = new { score = new { type = "number", state = "value", value = 9 } } }
    } } } } }, AggregateCanonical.Json);
var written = await payload.SaveReportPayloadAsync(f.Source, "[]", "{}", tables, null, f.Source.AssigneeUserId, DateTime.UtcNow, ct);
f.Source.PayloadRevision = written.PayloadRevision; f.Source.PayloadHash = written.PayloadHash; f.Source.PayloadSizeBytes = written.PayloadSizeBytes; f.Source.PayloadStatus = written.PayloadStatus;
await db.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == f.Source.Id, f.Source, cancellationToken: ct);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "ErrorLocationChecks" });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
var signing = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
builder.Configuration.AddJsonFile("appsettings.json", optional: false).AddInMemoryCollection(new Dictionary<string, string?> {
    ["AggregateMapping:V2Enabled"] = "true", ["AggregateMapping:Enabled"] = "false",
    ["AggregateMapping:ConfirmationKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<IDynamicFlowDefinitionTransactionRunner, DynamicFlowDefinitionTransactionRunner>();
builder.Services.AddSingleton<IWorkReportPayloadReader>(payload); builder.Services.AddSingleton<IWorkReportPayloadWriter>(payload);
builder.Services.AddSingleton<IWorkAssignmentReportService>(_ => {
    var constructor = typeof(WorkAssignmentReportService).GetConstructors().Single();
    return (IWorkAssignmentReportService)constructor.Invoke(constructor.GetParameters().Select(p => p.ParameterType == typeof(MongoDbContext) ? (object)db : null).ToArray());
});
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient().WithEndpoint(builder.Configuration["Minio:Endpoint"]!)
    .WithCredentials(builder.Configuration["Minio:AccessKey"]!, builder.Configuration["Minio:SecretKey"]!).WithSSL(builder.Configuration.GetValue<bool>("Minio:Secure")).Build());
builder.Services.AddHangfire(c => c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
    .UseMongoStorage(new MongoClient("mongodb://localhost:27017/?replicaSet=tdtd-rs"), "tdtd", new MongoStorageOptions { Prefix = run.Replace('-', '_'),
        CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.Poll, QueuePollInterval = TimeSpan.FromMilliseconds(200),
        MigrationOptions = new MongoMigrationOptions { MigrationStrategy = new MigrateMongoMigrationStrategy(), BackupStrategy = new CollectionMongoBackupStrategy() } }));
builder.Services.AddHangfireServer(o => { o.ServerName = run; o.WorkerCount = 1; o.SchedulePollingInterval = TimeSpan.FromSeconds(1); o.ShutdownTimeout = TimeSpan.FromSeconds(5); });
builder.Services.AddControllers().AddApplicationPart(typeof(AggregateMappingPreviewController).Assembly);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidIssuer = run, ValidateAudience = true, ValidAudience = run, ValidateIssuerSigningKey = true,
    IssuerSigningKey = signing, ValidateLifetime = true, ClockSkew = TimeSpan.Zero });
builder.Services.AddAuthorization();
await using var app = builder.Build();
app.Use(async (context, next) => { try { await next(context); } catch (AppException ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { code = ex.Code.ToString() }); }
    catch (Exception ex) { Console.Error.WriteLine(ex); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { code = "ERROR_LOCATION_CHECK_HOST_ERROR" }); } });
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync(ct);
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    Console.WriteLine("OWN_HOST " + address + " work=" + f.WorkId + " report=" + f.Report.Id + " source=" + f.Source.Id);
    using var http = new HttpClient { BaseAddress = new Uri(address + "/api/") };
    string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(run, run,
        [new Claim(JwtRegisteredClaimNames.Sub, actor), new Claim("sid", run + "-session")], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(browserHold ? 20 : 5), new SigningCredentials(signing, SecurityAlgorithms.HmacSha256)));
    async Task<JsonElement> Post(string path, object body, string? actor = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "aggregate-v2/" + path) { Content = JsonContent.Create(body, options: AggregateCanonical.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(actor ?? f.Actor));
        using var response = await http.SendAsync(request, ct); var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != expected) throw new Exception(path + ": expected " + (int)expected + ", got " + (int)response.StatusCode + " " + text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
    var runner = app.Services.GetRequiredService<IDynamicFlowDefinitionTransactionRunner>();
    var store = new AggregateMongoStore(db, runner, payload, payload); await store.CreateIndexesAsync(ct);
    var declaration = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run, 1);
    var period = await db.WorkReportPeriods.Find(p => p.Id == f.Source.WorkReportPeriodId).SingleAsync(ct);
    await store.ExecuteAsync(async (tx, cancel) => {
        foreach (var pin in new[] { ("REPORT", f.Source.Id), ("SLOT", period.WorkTemplateAssigneeId + ":ONCE") })
            await tx.PutAsync(AggregateCollections.Declarations, pin.Item1 + ":" + pin.Item2, 0,
                new AggregateDeclarationState(pin.Item1, pin.Item2, f.WorkId, f.Source.WorkAssignmentId, declaration), f.WorkId, pin.Item2, [], cancel);
        return true;
    }, ct);
    var boot = await Post("editor/bootstrap", new { reportId = f.Report.Id });
    var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
    var confirmation = await Post("data-window/preview", new AggregateDeclarationPreviewCommandDto(context, declaration));
    await Post("data-window/apply", new AggregateDeclarationSaveCommandDto(run + "-window", context, declaration, confirmation.GetString()!));
    boot = await Post("editor/bootstrap", new { reportId = f.Report.Id }); context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
    AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
    var recipe = new AggregateRecipeDto { SchemaVersion = 2, SemanticProfile = "REPORT_MAPPING_LIST_V1", Nodes = [
        new() { Id = "s", Kind = "SOURCE", Form = Pin(f.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "LIST", "SET", "items", "w")] },
        new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "LIST", "SET")], Outputs = [new("out", "NUMBER", "SINGLE")], Expressions = [new("out", new() { Kind = "LIST_PIPELINE", Ref = "in", ListPipeline = new() {
            Version = 1, Scope = "ALL_SOURCES", Take = "ALL", Sort = [], Project = [new("score", "score")], Operation = "ONLY_ITEM", ValueFieldId = "score" } })] },
        new() { Id = "t", Kind = "TARGET", Form = Pin(f.TargetForm), Inputs = [new("in", "NUMBER", "SINGLE", "total")], Outputs = [] } ],
        Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "out"), new("t", "in"))],
        TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
    var expected = boot.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
    var raw = new AggregatePreviewRequestDto(context, "draft-preview", "draft-config", expected, recipe, new([new("s", "FORM_SELECTOR", [], [])]));
    var request = new AggregatePreviewJobRequest("RECIPE", context, raw, null, null);
    var before = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
    async Task<JsonElement> Finish(JsonElement job) {
        while (job.GetProperty("state").GetString() is "QUEUED" or "RUNNING") { await Task.Delay(150, ct); job = await Post("preview-jobs/" + job.GetProperty("id").GetString() + "/read", new { }); }
        return job;
    }
    var job = await Finish(await Post("preview-jobs/start", request)); var jobId = job.GetProperty("id").GetString()!;
    Check(job.GetProperty("state").GetString() == "FAILED" && job.GetProperty("errorCode").GetString() == "LIST_SINGLE_ITEM_REQUIRED", "real Hangfire ONLY_ITEM failure preserves code");
    Check(job.GetProperty("errorPath").GetString() == "$.nodes[c].expressions[out].expression" && job.GetProperty("errorLocation").GetProperty("nodeId").GetString() == "c"
        && job.GetProperty("errorLocation").GetProperty("portId").GetString() == "out", "job read carries stable calculation/output location");
    Check(job.GetProperty("result").ValueKind == JsonValueKind.Null && job.GetProperty("progress").ValueKind == JsonValueKind.Null, "failed job exposes no successful or provisional values");
    var current = await Post("preview-jobs/current", new AggregateInstanceReadCommandDto(context));
    Check(current.GetProperty("current").GetProperty("id").GetString() == jobId && current.GetProperty("current").GetProperty("errorLocation").GetProperty("nodeId").GetString() == "c", "reopen current keeps failed location");
    await Post("preview-jobs/" + jobId + "/read", new { }, f.Outsider, HttpStatusCode.NotFound); Check(true, "outsider cannot read failure metadata");
    var jobs = new AggregatePreviewJobs(db, payload, builder.Configuration);
    var retried = await jobs.Start(request, f.Actor, run + "-session", ct);
    var queued = JsonSerializer.SerializeToElement(await jobs.Read(retried, f.Actor, run + "-session", ct), AggregateCanonical.Json);
    Check(retried == jobId && queued.GetProperty("state").GetString() == "QUEUED" && queued.GetProperty("errorCode").ValueKind == JsonValueKind.Null
        && queued.GetProperty("errorPath").ValueKind == JsonValueKind.Null && queued.GetProperty("errorLocation").ValueKind == JsonValueKind.Null, "retry clears old failure location before dispatch");
    await Post("preview-jobs/" + jobId + "/cancel", new { });
    var cancelled = await Post("preview-jobs/" + jobId + "/read", new { });
    Check(cancelled.GetProperty("state").GetString() == "CANCELLED" && cancelled.GetProperty("errorLocation").ValueKind == JsonValueKind.Null, "cancel queued retry does not expose stale location");
    job = await Finish(await Post("preview-jobs/start", request));
    Check(job.GetProperty("state").GetString() == "FAILED" && job.GetProperty("attempt").GetInt32() == 2 && job.GetProperty("errorLocation").GetProperty("nodeId").GetString() == "c", "retry failure gets current location from engine again");
    var sumRecipe = recipe with { Nodes = recipe.Nodes.Select(n => n.Kind == "CALCULATION" ? n with { Expressions = n.Expressions!.Select(e => e with {
        Expression = e.Expression with { ListPipeline = e.Expression.ListPipeline! with { Operation = "SUM" } } }).ToList() } : n).ToList() };
    var success = await Finish(await Post("preview-jobs/start", request with { Recipe = raw with { Recipe = sumRecipe } }));
    Check(success.GetProperty("state").GetString() == "COMPLETED" && success.GetProperty("errorCode").ValueKind == JsonValueKind.Null
        && success.GetProperty("errorPath").ValueKind == JsonValueKind.Null && success.GetProperty("errorLocation").ValueKind == JsonValueKind.Null, "next successful preview has no stale failure metadata");
    Check((await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision == before.PayloadRevision, "failure/reopen/retry/cancel/success preview never write report payload");
    // Read compatibility for an old row lacking additive metadata, scoped to this fixture only.
    await db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection).UpdateOneAsync(new BsonDocument("_id", jobId), new BsonDocument("$unset", new BsonDocument { ["errorPath"] = "", ["errorLocation"] = "" }), cancellationToken: ct);
    var old = JsonSerializer.SerializeToElement(await jobs.Read(jobId, f.Actor, run + "-session", ct), AggregateCanonical.Json);
    Check(old.GetProperty("errorCode").GetString() == "LIST_SINGLE_ITEM_REQUIRED" && old.GetProperty("errorLocation").ValueKind == JsonValueKind.Null, "legacy failed job without metadata remains readable");
    // Restore this fixture's real engine-generated location by a fresh retry.
    await Finish(await Post("preview-jobs/start", request));
    Directory.CreateDirectory("../outputs/aggregate-operators-20261005");
    await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/error-navigation-" + run + ".json", JsonSerializer.Serialize(new {
        run, workId = f.WorkId, targetReportId = f.Report.Id, sourceReportId = f.Source.Id, jobId, checks,
        failedResponse = job, recipe, context }, new JsonSerializerOptions(AggregateCanonical.Json) { WriteIndented = true }), ct);
    await FixtureProvenanceChecks.Migration(db, "R7 after fixture", ct);
    Console.WriteLine("PASS R7 API/JOB/MONGO " + checks);
    if (browserHold)
    {
        var cfg = await Post("configs", new AggregateConfigCreateCommandDto(context, new(run + "-browser-config", f.Binding.Id, Pin(f.TargetForm), recipe)));
        var instance = await Post("instances", new AggregateInstanceCreateCommandDto(run + "-browser-instance", context, cfg.GetProperty("id").GetString()!));
        var change = new AggregateMappingChangeDto(1, null, raw.Selection, [], false);
        var browserJob = await Finish(await Post("preview-jobs/start", new AggregatePreviewJobRequest("MAPPING", context, null, instance.GetProperty("id").GetString()!, change)));
        Check(browserJob.GetProperty("state").GetString() == "FAILED" && browserJob.GetProperty("errorLocation").GetProperty("nodeId").GetString() == "c", "persisted configuration job locates same calculation for real browser");
        Directory.CreateDirectory("../tdtd-fe/.tmp");
        await File.WriteAllTextAsync("../tdtd-fe/.tmp/r7-browser.private.json", JsonSerializer.Serialize(new { address, authorization = "Bearer " + Token(f.Actor), reportId = f.Report.Id }), ct);
        Console.WriteLine("BROWSER_READY own fixture only; host remains for 15 minutes. No tokens printed.");
        await Task.Delay(TimeSpan.FromMinutes(15), ct);
    }
}
finally { await app.StopAsync(CancellationToken.None); }