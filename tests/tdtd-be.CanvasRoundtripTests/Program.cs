using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Labels;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.CanvasRoundtripTests;

// Actual MVC controller + services + Mongo transaction runner, with test identity
// injection only. This deliberately does not execute the application's Program,
// JWT/login stack, background jobs, catalog overrides or production startup.
if (args.Length != 2) throw new ArgumentException("Expected owned run directory and mongod executable.");
await using var mongo = await CanvasMongo.StartAsync(args[0], args[1]);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Testing", ContentRootPath = mongo.Root });
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton<IOptions<MongoOptions>>(Options.Create(new MongoOptions { ConnectionString = mongo.Connection, Database = mongo.Database }));
builder.Services.AddScoped<MongoDbContext>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<MeAccessor>();
builder.Services.AddScoped<ILabelEnumCatalogService, LabelEnumCatalogService>();
builder.Services.AddScoped<ILabelConfigCommandService, LabelConfigCommandService>();
builder.Services.AddScoped<StatConfigTransactionRunner>();
builder.Services.AddScoped<IStatConfigTransactionRunner, AbortBeforeCommitRunner>();
builder.Services.AddScoped<IDynamicFormStatisticConfigCommandService, DynamicFormStatisticConfigCommandService>();
builder.Services.AddScoped<IDynamicFormService, DynamicFormService>();
builder.Services.AddSingleton(new TestKey(Convert.ToHexString(RandomNumberGenerator.GetBytes(24))));
builder.Services.AddAuthentication("CanvasTest").AddScheme<AuthenticationSchemeOptions, CanvasAuthentication>("CanvasTest", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddControllers().AddApplicationPart(typeof(DynamicFormController).Assembly)
    .ConfigureApplicationPartManager(parts => parts.FeatureProviders.Add(new OnlyCanvasController()));
await using var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (AppException error)
    {
        context.Response.StatusCode = error.Descriptor.HttpStatus;
        await context.Response.WriteAsJsonAsync(new { code = error.Code.ToString(), details = error.Details, error.Message });
    }
    catch (Exception error)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { code = error.GetType().Name, error.Message });
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
CanvasArtifactFixture.Map(app);
CanvasSnapshotFixture.Map(app);
// Raw label fixtures/oracle only in the isolated host, never a production route.
app.MapPost("/__canvas/label-storage/{id}", async (string id, JsonElement body, MongoDbContext db) =>
{
    var updates = new BsonDocument();
    if (body.TryGetProperty("setExtendedJson", out var set)) updates["$set"] = BsonDocument.Parse(set.GetString()!);
    if (body.TryGetProperty("unset", out var unset)) updates["$unset"] = new BsonDocument(unset.EnumerateArray().Select(x => new BsonElement(x.GetString()!, "")));
    await db.Db.GetCollection<BsonDocument>(db.Labels.CollectionNamespace.CollectionName).UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)), updates);
    return Results.Ok();
}).RequireAuthorization();
app.MapGet("/__canvas/label-storage/{id}", async (string id, MongoDbContext db) =>
{
    var stored = await db.Db.GetCollection<BsonDocument>(db.Labels.CollectionNamespace.CollectionName)
        .Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
    return new { bsonBase64 = Convert.ToBase64String(stored.ToBson()),
        extendedJson = stored.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }) };
}).RequireAuthorization();
// Explicit test-only registry; do not derive expected reference paths from the production scanner.
string ReferenceCollection(string kind, MongoDbContext db) => kind switch
{
    "lineage" => db.DynamicFormTemplates.CollectionNamespace.CollectionName,
    "sections" => db.DynamicFormSections.CollectionNamespace.CollectionName,
    "assignments" => db.WorkAssignments.CollectionNamespace.CollectionName,
    "reports" => db.WorkAssignmentReports.CollectionNamespace.CollectionName,
    "reportSections" => db.WorkAssignmentReportSections.CollectionNamespace.CollectionName,
    "periods" => db.WorkReportPeriods.CollectionNamespace.CollectionName,
    "assignees" => db.WorkTemplateAssignees.CollectionNamespace.CollectionName,
    "aggregateConfigs" => db.WorkAssignmentAggregateConfigs.CollectionNamespace.CollectionName,
    "basicConfigs" => db.WorkAssignmentBasicSummaryConfigs.CollectionNamespace.CollectionName,
    "basicSnapshots" => db.WorkAssignmentBasicSummarySnapshots.CollectionNamespace.CollectionName,
    "advancedConfigs" => db.WorkAssignmentAdvancedSummaryConfigs.CollectionNamespace.CollectionName,
    "advancedDays" => db.WorkAssignmentAdvancedSummaryDayNodes.CollectionNamespace.CollectionName,
    "advancedMonths" => db.WorkAssignmentAdvancedSummaryMonthNodes.CollectionNamespace.CollectionName,
    "advancedYears" => db.WorkAssignmentAdvancedSummaryYearNodes.CollectionNamespace.CollectionName,
    "fieldValues" => db.WorkReportFieldStatValues.CollectionNamespace.CollectionName,
    "fieldAggregates" => db.WorkReportFieldStatAggregates.CollectionNamespace.CollectionName,
    "tableValues" => db.WorkReportTableStatValues.CollectionNamespace.CollectionName,
    "tableAggregates" => db.WorkReportTableStatAggregates.CollectionNamespace.CollectionName,
    "labelValues" => db.WorkReportLabelStatValues.CollectionNamespace.CollectionName,
    "labelAggregates" => db.WorkReportLabelStatAggregates.CollectionNamespace.CollectionName,
    "rebuildJobs" => db.WorkReportStatisticRebuildJobs.CollectionNamespace.CollectionName,
    "p8Receipts" => db.StatConfigCommandReceipts.CollectionNamespace.CollectionName,
    _ => throw new ArgumentException("Unknown isolated reference fixture")
};
app.MapPost("/__canvas/references/{kind}", async (string kind, JsonElement body, MongoDbContext db) =>
{
    var rows = body.GetProperty("documents").EnumerateArray().Select(item => BsonDocument.Parse(item.GetRawText())).ToArray();
    await db.Db.GetCollection<BsonDocument>(ReferenceCollection(kind, db)).InsertManyAsync(rows);
    return Results.Ok();
}).RequireAuthorization();
app.MapGet("/__canvas/references/{kind}/{id}", async (string kind, string id, MongoDbContext db) =>
{
    var row = await db.Db.GetCollection<BsonDocument>(ReferenceCollection(kind, db)).Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
    return new { bsonBase64 = Convert.ToBase64String(row.ToBson()) };
}).RequireAuthorization();
app.MapPost("/__canvas/runtime-read-grant/{id}", async (string id, MongoDbContext db) =>
{
    await db.Db.GetCollection<BsonDocument>(db.WorkTemplateAssignees.CollectionNamespace.CollectionName).InsertOneAsync(new BsonDocument
    {
        ["dynamicFormTemplateId"] = ObjectId.Parse(id), ["assigneeUserId"] = "222222222222222222222222",
        ["isActive"] = true, ["isDeleted"] = false
    });
    return Results.Ok();
}).RequireAuthorization();
// Physical corruption/read oracle for diagnostic tests only. Production has no raw writer.
app.MapPost("/__canvas/storage/{id}", async (string id, JsonElement body, MongoDbContext db) =>
{
    var collection = db.Db.GetCollection<BsonDocument>(db.DynamicFormTemplates.CollectionNamespace.CollectionName);
    var updates = new BsonDocument();
    if (body.TryGetProperty("setExtendedJson", out var set)) updates["$set"] = BsonDocument.Parse(set.GetString()!);
    if (body.TryGetProperty("unset", out var unset)) updates["$unset"] = new BsonDocument(unset.EnumerateArray().Select(x => new BsonElement(x.GetString()!, "")));
    await collection.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)), updates);
    return Results.Ok();
}).RequireAuthorization();
app.MapGet("/__canvas/storage/{id}", async (string id, MongoDbContext db) =>
{
    var stored = await db.Db.GetCollection<BsonDocument>(db.DynamicFormTemplates.CollectionNamespace.CollectionName)
        .Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
    return new { bsonBase64 = Convert.ToBase64String(stored.ToBson()) };
}).RequireAuthorization();
// Source fixtures for the real Form import service. These keyed endpoints exist
// only in this isolated executable; they do not claim coverage of the Excel API.
app.MapPost("/__canvas/excel", async (JsonElement body, MongoDbContext db, MeAccessor me) =>
{
    var excel = new DynamicExcelTemplate
    {
        Id = ObjectId.GenerateNewId().ToString(), Code = "IMPORT-TEST", Name = "Bảng nhập thử",
        CreatedByUserId = me.RequireMe().Id, CreatedByUsername = "canvas-test-owner", CreatedAtUtc = DateTime.UtcNow,
        RawWorkbookDataJson = "[]", SpecJson = body.GetProperty("specJson").GetString()!,
        TableMode = body.GetProperty("tableMode").GetString()!, W = 2, H = 1,
        DataRectR0 = 0, DataRectC0 = 0, DataRectR1 = 0, DataRectC1 = 1,
    };
    await db.DynamicExcelTemplates.InsertOneAsync(excel);
    return new { id = excel.Id };
}).RequireAuthorization();
app.MapGet("/__canvas/excel/{id}", async (string id, MongoDbContext db) =>
{
    var excel = await db.DynamicExcelTemplates.Find(x => x.Id == id).SingleAsync();
    return new { excel.Id, excel.Code, excel.Name, excel.TableMode, excel.SpecJson, excel.W, excel.H,
        dataRect = new { r0 = excel.DataRectR0, c0 = excel.DataRectC0, r1 = excel.DataRectR1, c1 = excel.DataRectC1 } };
}).RequireAuthorization();
// Diagnostics exist only in this separately built executable and require its
// random key. They read fresh Mongo state, not the writer's response object.
app.MapGet("/__canvas/db/{id}", async (string id, MongoDbContext db) => new
{
    owner = await db.DynamicFormTemplates.Find(x => x.Id == id).SingleAsync(),
    receipts = await db.StatConfigCommandReceipts.Find(x => x.OwnerId == id).ToListAsync()
}).RequireAuthorization();
app.MapGet("/__canvas/inventory", async (MongoDbContext db) => new
{
    forms = await db.DynamicFormTemplates.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty),
    collections = await (await db.Db.ListCollectionNamesAsync()).ToListAsync()
}).RequireAuthorization();
app.MapGet("/__canvas/fingerprint/{id}", async (string id, MongoDbContext db) =>
{
    var owner = await db.DynamicFormTemplates.Find(x => x.Id == id).SingleAsync();
    return new { hash = Convert.ToHexString(SHA256.HashData(owner.ToBson())), owner.Revision,
        owner.StatisticConfigRevision, versions = owner.StatisticConfigSnapshots.Count,
        receipts = await db.StatConfigCommandReceipts.CountDocumentsAsync(x => x.OwnerId == id) };
}).RequireAuthorization();
app.MapGet("/__canvas/snapshot/{id}", async (string id, MongoDbContext db) =>
{
    var owner = await db.DynamicFormTemplates.Find(x => x.Id == id).SingleAsync();
    var form = DynamicFormPublishedSchemaSnapshotBuilder.Build(owner);
    var sections = DynamicFormSectionSnapshotBuilder.Build(owner);
    return new { form, sections = sections.Sections };
}).RequireAuthorization();
app.MapPost("/__canvas/labels", async (JsonElement body, ILabelConfigCommandService labels) =>
    await labels.CreateAsync(StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelConfigPayload>>(body), default))
    .RequireAuthorization();
app.MapPut("/__canvas/labels/{id}", async (string id, JsonElement body, ILabelConfigCommandService labels) =>
    await labels.UpdateAsync(id, StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelConfigPayload>>(body), default))
    .RequireAuthorization();
// Deliberate corruption uses a separate test owner for each case; never repairs
// or deletes that owner, so rejected reads and original raw bytes are inspectable.
app.MapPost("/__canvas/corrupt/{id}/{kind}", async (string id, string kind, MongoDbContext db) =>
{
    var owner = await db.DynamicFormTemplates.Find(x => x.Id == id).SingleAsync();
    switch (kind)
    {
        case "tables-json": owner.TablesJson = "{broken raw canvas"; break;
        case "missing-plan": owner.StatisticConfigSections!.NativePlanSectionJson = null; break;
        case "missing-snapshot": owner.StatisticConfigSnapshots.Clear(); break;
        case "history-hash": owner.StatisticConfigSnapshots[0].ConfigHash = new string('0', 64); break;
        default: throw new ArgumentException("Unknown corruption fixture.");
    }
    await db.DynamicFormTemplates.ReplaceOneAsync(x => x.Id == id, owner);
    return Results.Ok();
}).RequireAuthorization();
await app.StartAsync();
var key = app.Services.GetRequiredService<TestKey>().Value;
await File.WriteAllTextAsync(Path.Combine(mongo.Root, "ready.json"), JsonSerializer.Serialize(new
{ url = app.Urls.Single(), key, database = mongo.Database, mongoPid = mongo.Pid, hostPid = Environment.ProcessId }));
Console.WriteLine("CANVAS_READY");
// Parent owns this exact process and sends stop; closing stdin also stops it.
await Task.Run(Console.ReadLine);
await app.StopAsync();

sealed record TestKey(string Value);
sealed class OnlyCanvasController : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.ToArray())
            if (controller.AsType() != typeof(DynamicFormController)) feature.Controllers.Remove(controller);
    }
}
sealed class CanvasAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, TestKey key) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Canvas-Key"] != key.Value) return Task.FromResult(AuthenticateResult.NoResult());
        var actor = Request.Headers["X-Canvas-Actor"].ToString();
        var id = actor == "other" ? "222222222222222222222222" : "111111111111111111111111";
        Context.Items[MeAccessor.MeItemKey] = new MeResponse(id, "canvas-test-" + actor, "Canvas test", [],
            "333333333333333333333333", null, null, null, actor == "admin" ? ["SYSTEM_ADMIN"] : [], null, false);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], Scheme.Name)), Scheme.Name)));
    }
}
sealed class AbortBeforeCommitRunner(StatConfigTransactionRunner actual, IHttpContextAccessor http) : IStatConfigTransactionRunner
{
    public Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct = default)
        => actual.ExecuteAsync(async (session, token) =>
        {
            var result = await operation(session, token);
            if (http.HttpContext?.Request.Headers["X-Canvas-Transaction"].ToString() == "abort-before-commit")
                throw new InvalidOperationException("CANVAS_TEST_ABORT_AFTER_OWNER_AND_RECEIPT_BEFORE_COMMIT");
            return result;
        }, ct);
}
