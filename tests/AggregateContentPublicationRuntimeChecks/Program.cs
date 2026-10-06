// R6 own fixture; adapted from the richText handoff harness without editing that owner's files.
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

var scalarOnly = args.SequenceEqual(new[] { "--database", "tdtd", "--run-own-content-fixture", "--scalar-only" });
if (!scalarOnly && !args.SequenceEqual(new[] { "--database", "tdtd", "--run-own-content-fixture" }))
    throw new ArgumentException("Use --database tdtd --run-own-content-fixture. Creates only a named richText fixture; no reset or cleanup.");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var ct = timeout.Token;
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
var hello = await db.Db.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct);
if (hello.GetValue("setName", "") != "tdtd-rs" || !hello.GetValue("isWritablePrimary", false).AsBoolean) throw new Exception("Requires authorized local writable tdtd replica set.");
await FixtureProvenanceChecks.Migration(db, "R6 before fixture", ct);
var run = "r6-content-20261005-" + Guid.NewGuid().ToString("N")[..8]; var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
// Read-only audit of the two failed attempts; never replay or repair another owner's fixture.
foreach (var failedReport in new[] { "6ac37a28eb5d80cbe46e9d1e", "6ac3b5b0f01bdc75780ec31b" })
{
    var old = await db.WorkAssignmentReports.Find(r => r.Id == failedReport).SingleAsync(ct);
    var oldPayload = await new WorkReportPayloadService(db).LoadReportPayloadAsync(old, ct);
    var instances = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("target", failedReport)).ToListAsync(ct);
    var receipts = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Receipts).Find(new BsonDocument("workId", old.WorkId)).ToListAsync(ct);
    var nativeCount = await db.Db.GetCollection<BsonDocument>(AggregateCollections.NativeContent).CountDocumentsAsync(new BsonDocument("target", failedReport), cancellationToken: ct);
    var snapshotCount = await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots).CountDocumentsAsync(new BsonDocument("target", failedReport), cancellationToken: ct);
    Check(old.PayloadRevision == 1 && oldPayload.FieldValuesJson == "{}" && nativeCount == 0 && snapshotCount == 0
        && instances.All(r => AggregateMongoTransaction.Read<AggregateInstanceState>(r).Value is { Revision: 1, Applied: null })
        && receipts.All(r => AggregateMongoTransaction.Read<AggregateReceipt>(r).Value.Operation != "APPLY"),
        "failed Apply rolled back payload/binding/snapshot/instance/receipt: " + failedReport);
}
var table = new DynamicFormNativeTableDto { Id = "body", SectionId = "main", Name = "Nội dung", Layout = "vertical", Order = 0,
    Fields = [new() { Id = "unit", Name = "Đơn vị báo cáo", Order = 0 }, new() { Id = "content", Name = "Nội dung", Order = 1 }], Rows = [], StatisticTargets = [],
    TypeConfig = new() { Version = 1, Sequence = 2, Rules = [new() { Order = 1, Target = new() { Scope = "column", FieldId = "unit" }, Spec = new() { Type = "plainText", Required = false } },
        new() { Order = 2, Target = new() { Scope = "column", FieldId = "content" }, Spec = new() { Type = "plainText", Required = false } }] } };
var f = await RuntimeFixture.Seed(db, run, ct, configureForm: (form, target) => {
    form.Name = "[DỮ LIỆU THỬ RIÊNG richText] " + (target ? "Tổng hợp" : "Nguồn");
    form.FieldsJson = target
        ? "[{\"id\":\"total\",\"key\":\"total\",\"name\":\"Văn bản tổng hợp\",\"label\":\"Văn bản tổng hợp\",\"sectionId\":\"main\",\"type\":\"richText\",\"required\":false},{\"id\":\"plain\",\"key\":\"plain\",\"name\":\"Nội dung\",\"label\":\"Nội dung\",\"sectionId\":\"main\",\"type\":\"longText\",\"required\":false}]"
        : "[{\"id\":\"n\",\"key\":\"n\",\"name\":\"Nội dung nguồn\",\"label\":\"Nội dung nguồn\",\"sectionId\":\"main\",\"type\":\"richText\",\"required\":false}]";
    if (target) { form.NativeTablesVersion = 1; form.TablesJson = JsonSerializer.Serialize(new[] { table }, AggregateCanonical.Json); }
});
var payload = new WorkReportPayloadService(db);
const string originalHtml = "<p><strong>đã</strong> xử lý</p><p>A &lt; B &amp; C</p><ul><li>Mục một</li><li>Mục hai</li></ul>";
const string visible = "đã xử lý\nA < B & C\nMục một\nMục hai";
var sourceJson = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(JsonSerializer.Serialize(new { n = originalHtml }), [new("n", "richText")], false).CanonicalValuesJson;
var written = await payload.SaveReportPayloadAsync(f.Source, "[]", sourceJson, null, null, f.Source.AssigneeUserId, DateTime.UtcNow, ct);
f.Source.PayloadRevision = written.PayloadRevision; f.Source.PayloadHash = written.PayloadHash; f.Source.PayloadSizeBytes = written.PayloadSizeBytes; f.Source.PayloadStatus = written.PayloadStatus;
await db.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == f.Source.Id, f.Source, cancellationToken: ct);
var sourceBefore = await payload.LoadReportPayloadAsync(f.Source, ct);
Directory.CreateDirectory("../outputs/aggregate-operators-20261005");
var manifestPath = "../outputs/aggregate-operators-20261005/content-publication-" + run + ".json";
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { run, scalarOnly, workId = f.WorkId, reportId = f.Report.Id,
    sourceReportId = f.Source.Id, state = "SEE_RUNTIME_LOG" }, new JsonSerializerOptions { WriteIndented = true }), ct);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "RichTextChecks" });
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
    catch (Exception ex) { Console.Error.WriteLine(ex); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { code = "RICH_TEXT_CHECK_HOST_ERROR" }); } });
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync(ct);
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    Console.WriteLine("OWN_HOST " + address + " work=" + f.WorkId + " report=" + f.Report.Id + " source=" + f.Source.Id);
    using var http = new HttpClient { BaseAddress = new Uri(address + "/api/") };
    string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(run, run,
        [new Claim(JwtRegisteredClaimNames.Sub, actor), new Claim("sid", run + "-session")], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(signing, SecurityAlgorithms.HmacSha256)));
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
    var recipe = new AggregateRecipeDto { SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1", Nodes = [
        new() { Id = "s", Kind = "SOURCE", Form = Pin(f.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "TEXT", "SET", "n", "w")] },
        new() { Id = "filter", Kind = "FILTER", Inputs = [new("in", "TEXT", "SET")], Outputs = [new("out", "TEXT", "SET")], Predicate = new() {
            Kind = "CALL", Name = "TEXT_CONTAINS", Arguments = [new() { Kind = "INPUT", Ref = "in" }, new() { Kind = "TEXT", Value = "đã xử lý" }] } },
        new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "TEXT", "SET")], Outputs = [new("text", "TEXT", "SINGLE"), new("table", "TABLE", "SINGLE")], Expressions = [
            new("text", new() { Kind = "CALL", Name = "CONCAT", Arguments = [new() { Kind = "INPUT", Ref = "in" }], Options = new() { Order = "UNIT_THEN_PERIOD", Separator = "\n", Trim = false } }),
            new("table", new() { Kind = "CALL", Name = "REPORT_TEXT_TABLE", Arguments = [new() { Kind = "INPUT", Ref = "in" }], Options = new() { Order = "UNIT_THEN_PERIOD" } })] },
        new() { Id = "t", Kind = "TARGET", Form = Pin(f.TargetForm), Inputs = [new("rich", "TEXT", "SINGLE", "total"), new("plain", "TEXT", "SINGLE", "plain"), new("table", "TABLE", "SINGLE", "body")], Outputs = [] } ],
        Edges = [new("a", new("s", "out"), new("filter", "in")), new("b", new("filter", "out"), new("c", "in")),
            new("rich", new("c", "text"), new("t", "rich")), new("plain", new("c", "text"), new("t", "plain")), new("table", new("c", "table"), new("t", "table"))],
        TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
    if (scalarOnly) recipe = recipe with { Nodes = recipe.Nodes.Select(n => n.Kind == "CALCULATION" ? n with {
        Outputs = n.Outputs.Where(p => p.ValueType == "TEXT").ToList(), Expressions = n.Expressions!.Where(e => e.PortId == "text").ToList()
    } : n.Kind == "TARGET" ? n with { Inputs = n.Inputs.Where(p => p.ValueType == "TEXT").ToList() } : n).ToList(), Edges = recipe.Edges.Where(e => e.Id != "table").ToList() };
    var cfg = await Post("configs", new AggregateConfigCreateCommandDto(context, new(run + "-config", f.Binding.Id, Pin(f.TargetForm), recipe)));
    var instance = await Post("instances", new AggregateInstanceCreateCommandDto(run + "-instance", context, cfg.GetProperty("id").GetString()!));
    var id = instance.GetProperty("id").GetString()!; var change = new AggregateMappingChangeDto(1, null, new([new("s", "FORM_SELECTOR", [], [])]), [], false);
    var before = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
    var job = await Post("preview-jobs/start", new AggregatePreviewJobRequest("MAPPING", context, null, id, change)); var jobId = job.GetProperty("id").GetString()!;
    while (job.GetProperty("state").GetString() is "QUEUED" or "RUNNING") { await Task.Delay(200, ct); job = await Post("preview-jobs/" + jobId + "/read", new { }); }
    Check(job.GetProperty("state").GetString() == "COMPLETED", "own Hangfire richText job completed: " + job.GetProperty("errorCode"));
    var prepared = job.GetProperty("result"); var results = prepared.GetProperty("preview").GetProperty("preview").GetProperty("results").EnumerateArray().ToArray();
    Check(results.Single(r => r.GetProperty("portId").GetString() == "rich").GetProperty("value").GetString() == visible, "API preview rich field uses visible words and line breaks");
    Check(results.Single(r => r.GetProperty("portId").GetString() == "plain").GetProperty("value").GetString() == visible, "API CONCAT output supports plain field");
    AggregateContentReadRequest? contentRequest = null;
    if (!scalarOnly)
    {
        var reference = results.Single(r => r.GetProperty("portId").GetString() == "table").GetProperty("value");
        contentRequest = new(f.Report.Id, null, null, jobId, reference);
        var page = await Post("content/page", contentRequest);
        Check(page.GetProperty("rows")[0].GetProperty("sample").GetString() == visible, "preview content page has words, no markup");
        var rowKey = page.GetProperty("rows")[0].GetProperty("rowKey").GetString()!;
        Check((await Post("content/part", contentRequest with { RowKey = rowKey })).GetProperty("text").GetString() == visible, "preview content part retains paragraphs");
    }
    Check((await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision == before.PayloadRevision, "preview did not write report payload");
    if (contentRequest != null) { await Post("content/page", contentRequest, f.Outsider, HttpStatusCode.NotFound); Check(true, "source form access does not bypass content ACL"); }
    var apply = new AggregateMappingApplyCommandDto(run + "-apply", context, change, prepared.GetProperty("token").GetString()!);
    await Post("instances/" + id + "/apply", apply);
    var saved = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct); var snapshot = await payload.LoadReportPayloadAsync(saved, ct);
    var fields = JsonSerializer.Deserialize<JsonElement>(snapshot.FieldValuesJson!);
    Check(fields.GetProperty("plain").GetString() == visible, "Apply/readback plain field retains visible content");
    var richSaved = fields.GetProperty("total").GetString()!;
    Check(richSaved == AggregateTextProjection.RichDestination(visible), "Apply/readback rich field escapes literal characters and retains lines");
    var canonical = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(snapshot.FieldValuesJson, [new("total", "richText"), new("plain", "longText")], false);
    Check(canonical.SanitizedRichTextFieldIds.Count == 0, "stored rich result is already canonical");
    Check(saved.PayloadRevision == before.PayloadRevision + 1, "Apply increments payload once");
    if (contentRequest != null) { var reportContent = contentRequest with { JobId = null, TableId = "body", PayloadRevision = saved.PayloadRevision };
        var reportPage = await Post("content/page", reportContent);
        Check(reportPage.GetProperty("rows")[0].GetProperty("sample").GetString() == visible, "saved report content table reads same words");
        var rowKey = reportPage.GetProperty("rows")[0].GetProperty("rowKey").GetString()!;
        Check((await Post("content/part", reportContent with { RowKey = rowKey })).GetProperty("text").GetString() == visible, "saved content part readback retains full paragraphs");
        var denied = await Post("content/page", reportContent, f.Outsider, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN", "saved content readback denies outsider"); }
    var replay = await Post("instances/" + id + "/apply", apply);
    Check(replay.GetProperty("replayed").GetBoolean() && (await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision == saved.PayloadRevision, "Apply retry does not duplicate payload");
    async Task<AggregateInstanceState> StoredInstance() => (await store.ExecuteAsync((tx, cancel) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, id, cancel), ct))!.Value;
    var configId = cfg.GetProperty("id").GetString()!;
    boot = await Post("editor/bootstrap", new { reportId = f.Report.Id }); context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
    var impact = new AggregateConfigImpactRequestDto(1, recipe, [new(id, (await StoredInstance()).Revision)], []);
    var previewImpact = await Post("configs/" + configId + "/impact-preview", new AggregateConfigImpactCommandDto(context, impact));
    var revisionCommand = new AggregateConfigRevisionCommandDto(run + "-r2", context, impact, previewImpact.GetProperty("token").GetString()!);
    await Post("configs/" + configId + "/revisions", revisionCommand);
    var migrated = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
    Check((await StoredInstance()).ConfigRevision == 2 && migrated.PayloadRevision == saved.PayloadRevision + 1,
        "common r2 selected Draft migration writes content and config atomically");
    Check((await Post("configs/" + configId + "/revisions", revisionCommand)).GetProperty("replayed").GetBoolean(), "config save replay returns receipt");
    var refresh = new AggregateRefreshService(store, new AggregateMongoCommandReader(db, payload, v2Enabled: true));
    for (var pass = 0; pass < 3; pass++)
    {
        var active = await StoredInstance(); var revisionBefore = (await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision;
        await store.ExecuteAsync(async (tx, cancel) => { await AggregateRefreshService.EnqueueAsync(tx, active, run + "-refresh-" + pass, cancel); return true; }, ct);
        Check(await refresh.RunAsync(id, active.Generation, ct) == "COMPLETED", "existing refresh handler publishes staged content pass " + pass);
        Check((await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision == revisionBefore + 1,
            "refresh increments payload once pass " + pass);
        Check(await refresh.RunAsync(id, active.Generation, ct) == "NO_WORK", "refresh replay does not publish twice pass " + pass);
    }
    saved = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
    if (contentRequest != null)
    {
        var minio = app.Services.GetRequiredService<IMinioClient>();
        var cleanup = new AggregateContentRetentionJob(db, minio, app.Configuration);
        await cleanup.Run(f.Report.Id, ct); // Existing job, own report only; never acceptance artifacts.
        var retained = await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots).Find(new BsonDocument("target", f.Report.Id)).ToListAsync(ct);
        Check(retained.Count == 3 && retained.Select(r => AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value.PayloadRevision)
            .OrderDescending().SequenceEqual(new long[] { saved.PayloadRevision, saved.PayloadRevision - 1, saved.PayloadRevision - 2 }), "retention keeps three total versions including current");
        var cleanupState = await db.Db.GetCollection<BsonDocument>(AggregateContentRetentionJob.Intents).Find(new BsonDocument("_id", f.Report.Id)).SingleAsync(ct);
        Check(cleanupState["state"] == "COMPLETED", "cleanup durable intent completed");
        await cleanup.Run(f.Report.Id, ct);
        Check(await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots).CountDocumentsAsync(new BsonDocument("target", f.Report.Id), cancellationToken: ct) == 3,
            "cleanup rerun preserves retained versions");
        var lastContent = contentRequest with { JobId = null, TableId = "body", PayloadRevision = saved.PayloadRevision };
        Check((await Post("content/page", lastContent)).GetProperty("rows")[0].GetProperty("sample").GetString() == visible,
            "current content survives cleanup and refresh");
        var uploader = new AggregateContentSnapshotJob(db, minio, app.Configuration);
        foreach (var retainedRow in retained) await uploader.Run(retainedRow["_id"].AsString, ct);
        var ready = await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots).Find(new BsonDocument("target", f.Report.Id)).ToListAsync(ct);
        Check(ready.Count == 3 && ready.All(r => r["state"] == "READY"), "retained snapshot jobs persist three artifacts to MinIO");
        var currentArtifact = ready.Single(r => AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value.PayloadRevision == saved.PayloadRevision);
        using var file = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs().WithBucket(currentArtifact["bucket"].AsString).WithObject(currentArtifact["objectKey"].AsString)
            .WithCallbackStream(stream => stream.CopyTo(file)), ct);
        Check(file.Length == currentArtifact["bytes"].ToInt64() && Convert.ToHexString(SHA256.HashData(file.ToArray())) == currentArtifact["sha256"].AsString,
            "current MinIO artifact readback matches saved length and SHA256");
        var active = await StoredInstance();
        boot = await Post("editor/bootstrap", new { reportId = f.Report.Id }); context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var currentChange = change with { ExpectedRevision = active.Revision };
        var staleApply = apply with { CommandId = run + "-stale", Context = context, Change = currentChange };
        var staleResult = await Post("instances/" + id + "/apply", staleApply, expected: HttpStatusCode.Conflict);
        Check(staleResult.GetProperty("code").GetString() == "AGG_CONFIRMATION_STALE", "old preview token cannot publish after revision changes");
        var fresh = await Post("instances/" + id + "/mapping/preview", new AggregateMappingPreviewCommandDto(context, currentChange));
        var gatedApply = staleApply with { CommandId = run + "-blocked", ConfirmationToken = fresh.GetProperty("token").GetString()! };
        await using (var held = await AggregateContentScopeGate.Acquire(db, f.Report.Id, ct) ?? throw new Exception("own scope gate unavailable"))
        {
            var blocked = await Post("instances/" + id + "/apply", gatedApply, expected: HttpStatusCode.Conflict);
            Check(blocked.GetProperty("code").GetString() == "AGG_INPUT_STALE", "held content scope rejects publication without disabling the gate");
        }
        var afterRejected = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
        Check(afterRejected.PayloadRevision == saved.PayloadRevision && (await StoredInstance()).Revision == active.Revision,
            "stale and gated attempts leave report and mapping unchanged");
        var newReceipt = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Receipts).Find(new BsonDocument("workId", f.WorkId)).ToListAsync(ct);
        Check(newReceipt.Count(r => AggregateMongoTransaction.Read<AggregateReceipt>(r).Value.Operation == "APPLY") == 1,
            "rejected attempts do not create successful Apply receipts");
    }
    var sourceAfter = await db.WorkAssignmentReports.Find(r => r.Id == f.Source.Id).SingleAsync(ct); var sourceSnapshot = await payload.LoadReportPayloadAsync(sourceAfter, ct);
    Check(sourceSnapshot.FieldValuesJson == sourceBefore.FieldValuesJson && sourceAfter.PayloadRevision == f.Source.PayloadRevision && sourceAfter.Status == f.Source.Status, "Approved source formatting and revision unchanged");
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { run, scalarOnly, state = "PASS", checks, workId = f.WorkId, reportId = f.Report.Id, sourceReportId = f.Source.Id, configId, instanceId = id, jobId }, new JsonSerializerOptions { WriteIndented = true }), ct);
    await FixtureProvenanceChecks.Migration(db, "R6 after runtime", ct);
    Console.WriteLine($"PASS: {checks} scoped API/job/Apply/readback assertions. Source seeded Approved; no login/lifecycle browser acceptance.");
}
finally { await app.StopAsync(CancellationToken.None); }
