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
using Minio;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class ApiChecks
{
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
    internal static async Task Run(MongoDbContext db, string run, CancellationToken ct, bool loadOnly = false, bool wideLoadOnly = false, bool contentOnly = false, bool mixedLoadOnly = false, bool contentRetry = false, bool listOnly = false, bool listL4FixtureOnly = false, bool listL4Periodic = false, bool listL4Paging = false)
    {
        var fixture = await RuntimeFixture.Seed(db, run, ct);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "P05" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        // Ephemeral signing and confirmation keys stay in this process, never logs or appsettings.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["AggregateMapping:V2Enabled"] = "false", ["AggregateMapping:Enabled"] = "true",
            ["AggregateMapping:ConfirmationKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<IDynamicFlowDefinitionTransactionRunner, DynamicFlowDefinitionTransactionRunner>();
        builder.Services.AddSingleton<IWorkReportPayloadReader, WorkReportPayloadService>();
        // Only the read-only content policy method is called here; unused mutation collaborators stay absent.
        builder.Services.AddSingleton<tdtd_be.Services.WorkAssignmentReports.IWorkAssignmentReportService>(_ => {
            var constructor = typeof(tdtd_be.Services.WorkAssignmentReports.WorkAssignmentReportService).GetConstructors().Single();
            return (tdtd_be.Services.WorkAssignmentReports.IWorkAssignmentReportService)constructor.Invoke(
                constructor.GetParameters().Select(p => p.ParameterType == typeof(MongoDbContext) ? (object)db : null).ToArray());
        });
        var faultWriter = new InjectedPayloadWriter(db);
        builder.Services.AddSingleton<IWorkReportPayloadWriter>(faultWriter);
        MinioUploadFault? uploadFault = null;
        if(contentOnly || mixedLoadOnly)
        {
            var storageConfig=new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build();
            builder.Configuration["Uploads:Bucket"]=storageConfig["Uploads:Bucket"]??"tdtd-attachments";
            builder.Services.AddSingleton<IMinioClient>(_=>{
                var client = new MinioClient().WithEndpoint(storageConfig["Minio:Endpoint"]!)
                    .WithCredentials(storageConfig["Minio:AccessKey"]!,storageConfig["Minio:SecretKey"]!).WithSSL(storageConfig.GetValue<bool>("Minio:Secure")).Build();
                if (!contentRetry) return client;
                var wrapped = MinioUploadFault.Wrap(client); uploadFault = wrapped.Fault; return wrapped.Client;
            });
        }
        if (contentOnly || mixedLoadOnly || listOnly || listL4FixtureOnly)
        {
            builder.Services.AddHangfire(c=>c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
                .UseMongoStorage(new MongoClient("mongodb://localhost:27017/?replicaSet=tdtd-rs"),"tdtd",new MongoStorageOptions{
                    Prefix=run+"_content_jobs",CheckQueuedJobsStrategy=CheckQueuedJobsStrategy.Poll,QueuePollInterval=TimeSpan.FromMilliseconds(200),
                    MigrationOptions=new MongoMigrationOptions{MigrationStrategy=new MigrateMongoMigrationStrategy(),BackupStrategy=new CollectionMongoBackupStrategy()}}));
            builder.Services.AddHangfireServer(o=>{o.ServerName=run+"-content";o.WorkerCount=1;o.Queues=["default"];o.SchedulePollingInterval=TimeSpan.FromSeconds(1);o.ShutdownTimeout=TimeSpan.FromSeconds(10);});
        }
        builder.Services.AddControllers().AddApplicationPart(typeof(AggregateMappingPreviewController).Assembly);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
            options.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = run, ValidateAudience = true, ValidAudience = run,
                ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidateLifetime = true, ClockSkew = TimeSpan.Zero };
        });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        // Only the two Aggregate controllers are exercised. This is a scoped host, not Program.cs startup or login UAT.
        app.Use(async (context, next) => {
            try { await next(context); }
            catch (AppException ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { code = ex.Code.ToString() }); }
            catch (InvalidOperationException ex) when (ex.Message == "P05_INJECTED_AFTER_NATIVE_WRITE") {
                context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { code = ex.Message });
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { code = "P05_HOST_EXCEPTION", message = ex.Message }); }
        });
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        await app.StartAsync(ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(run, run,
            [new Claim(JwtRegisteredClaimNames.Sub, actor), new Claim("sid", run + "-session")], DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        async Task<JsonElement> Send(HttpMethod method, string path, object body, string? actor, HttpStatusCode status)
        {
            using var req = new HttpRequestMessage(method, "/api/" + path) { Content = JsonContent.Create(body, options: AggregateCanonical.Json) };
            if (actor != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(actor));
            using var response = await http.SendAsync(req, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode != status) throw new InvalidOperationException($"{path}: expected {(int)status}, got {(int)response.StatusCode}: {text}");
            return string.IsNullOrWhiteSpace(text) ? default : response.Content.Headers.ContentType?.MediaType == "text/plain"
                ? JsonSerializer.SerializeToElement(text) : JsonSerializer.Deserialize<JsonElement>(text);
        }
        Task<JsonElement> Post(string path, object body, string? actor, HttpStatusCode status = HttpStatusCode.OK) => Send(HttpMethod.Post, path, body, actor, status);
        Task<JsonElement> Put(string path, object body, string? actor, HttpStatusCode status) => Send(HttpMethod.Put, path, body, actor, status);
        async Task<AggregatePeriodContextDto> Bootstrap() => (await Post("aggregate-v2/editor/bootstrap", new { reportId = fixture.Report.Id }, fixture.Actor))
            .GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        await Post("aggregate-v2/editor/bootstrap", new { reportId = fixture.Report.Id }, null, HttpStatusCode.Unauthorized);
        Check(true, "v2 HTTP without JWT is 401");
        var denied = await Post("aggregate-v2/editor/bootstrap", new { reportId = fixture.Report.Id }, fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_PREVIEW_INTEGRATION_PENDING", "v1 flag cannot activate v2");
        builder.Configuration["AggregateMapping:V2Enabled"] = "true";
        denied = await Post("aggregate-v1/editor/bootstrap", new { reportId = fixture.Report.Id }, fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE", "v2 activation leaves v1 phase barrier blocked");
        denied = await Post("aggregate-v2/editor/bootstrap", new { reportId = fixture.Report.Id }, fixture.Outsider, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_CONTEXT_UNAVAILABLE", "real Mongo policy denies unrelated actor");
        var context = await Bootstrap();
        Check(context.ReportId == fixture.Report.Id, "v2 JWT bootstrap resolves native report and published schema");
        var payload = new WorkReportPayloadService(db);
        var runner = app.Services.GetRequiredService<IDynamicFlowDefinitionTransactionRunner>();
        var store = new AggregateMongoStore(db, runner, payload, payload);
        if (listOnly || listL4FixtureOnly) {
            await ListRuntimeChecks.Run(db, runner, builder.Configuration, run, Post, ct, browserFixture: listL4FixtureOnly, browserPeriodic:listL4Periodic, browserPaging:listL4Paging);
            if(listOnly) await TableChecks.Run(db, runner, run + "-table-regression", Post, ct);
            await app.StopAsync(ct); return;
        }
        if (contentOnly) {
            _ = app.Services.GetRequiredService<IMinioClient>();
            await ContentTableApiChecks.Run(db, store, runner, builder.Configuration, run, Post, faultWriter, ct, uploadFault);
            await app.StopAsync(ct); return;
        }
        await store.CreateIndexesAsync(ct);
        if (wideLoadOnly || mixedLoadOnly)
        {
            await WideFormLoadChecks.Run(db, store, runner, builder.Configuration, run, Post, ct, mixedLoadOnly);
            await app.StopAsync(ct);
            return;
        }
        var declaration = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run, 1);
        // Source is an already-approved input fixture; its declared window is setup, not a lifecycle acceptance claim.
        await store.ExecuteAsync(async (tx, token) => { await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + fixture.Source.Id, 0,
            new AggregateDeclarationState("REPORT", fixture.Source.Id, fixture.WorkId, fixture.Source.WorkAssignmentId, declaration),
            fixture.WorkId, fixture.Source.Id, [], token); return true; }, ct);
        var sourcePeriod = await db.WorkReportPeriods.Find(p => p.Id == fixture.Source.WorkReportPeriodId).SingleAsync(ct);
        var slot = sourcePeriod.WorkTemplateAssigneeId + ":ONCE";
        await store.ExecuteAsync(async (tx, token) => { await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slot, 0,
            new AggregateDeclarationState("SLOT", slot, fixture.WorkId, fixture.Source.WorkAssignmentId, declaration),
            fixture.WorkId, slot, [], token); return true; }, ct);
        var confirmation = await Post("aggregate-v2/data-window/preview", new AggregateDeclarationPreviewCommandDto(context, declaration), fixture.Actor);
        await Post("aggregate-v2/data-window/apply", new AggregateDeclarationSaveCommandDto(run + "-window", context, declaration, confirmation.GetString()!), fixture.Actor);
        context = await Bootstrap();
        Check(context.DataStartDate == declaration.StartDate && context.DataEndDate == declaration.EndDate, "v2 declaration preview/confirm persists explicit data window");
        AggregateFormPinDto Pin(tdtd_be.Models.DynamicFormTemplate f) => new(f.Id, f.FamilyId!, f.VersionNo, f.PublishedSchemaHash!);
        var recipe = new AggregateRecipeDto {
            SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1",
            Nodes = [new() { Id = "s", Kind = "SOURCE", Form = Pin(fixture.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "NUMBER", "SET", "n", "w")] },
                new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "NUMBER", "SET")], Outputs = [new("out", "NUMBER", "SINGLE")], Expressions = [new("out", new() { Kind = "CALL", Name = "SUM", Arguments = [new() { Kind = "INPUT", Ref = "in" }] })] },
                new() { Id = "t", Kind = "TARGET", Form = Pin(fixture.TargetForm), Inputs = [new("in", "NUMBER", "SINGLE", "total")], Outputs = [] }],
            Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "out"), new("t", "in"))],
            TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
        if (loadOnly)
        {
            await LoadChecks.Run(db, store, fixture, recipe, context, http, () => Token(fixture.Actor), run, ct);
            await app.StopAsync(ct);
            return;
        }
        // Owning another published Form does not authorize replacing the assigned
        // report Form through aggregate config creation or revision endpoints.
        var otherTargetRecipe = recipe with { Nodes = recipe.Nodes.Select(n => n.Kind == "TARGET"
            ? n with { Form = Pin(fixture.SourceForm), Inputs = [new("in", "NUMBER", "SINGLE", "n")] } : n).ToList() };
        var assignedFormBefore = await Snapshot(db, fixture, ct);
        denied = await Post("aggregate-v2/configs", new AggregateConfigCreateCommandDto(context,
            new(run + "-replace-assigned-form", fixture.Binding.Id, Pin(fixture.SourceForm), otherTargetRecipe)), fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_CONTEXT_STALE" && assignedFormBefore == await Snapshot(db, fixture, ct),
            "Form owner cannot replace assigned report Form through config creation; fixture unchanged");
        var config = await Post("aggregate-v2/configs", new AggregateConfigCreateCommandDto(context, new(run + "-config", fixture.Binding.Id, Pin(fixture.TargetForm), recipe)), fixture.Actor);
        var invalidFormImpact = new AggregateConfigImpactRequestDto(1, otherTargetRecipe, [], []);
        assignedFormBefore = await Snapshot(db, fixture, ct);
        denied = await Post($"aggregate-v2/configs/{config.GetProperty("id").GetString()}/impact-preview",
            new AggregateConfigImpactCommandDto(context, invalidFormImpact), fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_SCHEMA_INCOMPATIBLE" && assignedFormBefore == await Snapshot(db, fixture, ct),
            "config revision preview rejects a different assigned target Form without writes");
        denied = await Post($"aggregate-v2/configs/{config.GetProperty("id").GetString()}/revisions",
            new AggregateConfigRevisionCommandDto(run + "-replace-assigned-form-r2", context, invalidFormImpact, "not-issued"), fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_SCHEMA_INCOMPATIBLE" && assignedFormBefore == await Snapshot(db, fixture, ct),
            "direct config revision commit cannot bypass the assigned Form or create a new revision");
        var instance = await Post("aggregate-v2/instances", new AggregateInstanceCreateCommandDto(run + "-instance", context, config.GetProperty("id").GetString()!), fixture.Actor);
        var instanceId = instance.GetProperty("id").GetString()!;
        var change = new AggregateMappingChangeDto(1, null, new([new("s", "FORM_SELECTOR", [], [])]), [], false);
        var before = await Snapshot(db, fixture, ct);
        var preview = await Post($"aggregate-v2/instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change), fixture.Actor);
        Check(before == await Snapshot(db, fixture, ct), "HTTP mapping preview has no writes to aggregate/native fixture records");
        Check(preview.GetProperty("preview").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "30", "HTTP preview reads Approved child payload and evaluates 30");
        var apply = new AggregateMappingApplyCommandDto(run + "-apply", context, change, preview.GetProperty("token").GetString()!);
        before = await Snapshot(db, fixture, ct);
        faultWriter.FailAfterWrite = true;
        try { await Post($"aggregate-v2/instances/{instanceId}/apply", apply, fixture.Actor, HttpStatusCode.InternalServerError); }
        finally { faultWriter.FailAfterWrite = false; }
        Check(before == await Snapshot(db, fixture, ct), "failure after native payload write rolls back payload, claims, fences, instance and receipt");
        await Post($"aggregate-v2/instances/{instanceId}/apply", apply, fixture.Actor);
        var native = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
        var readback = await payload.LoadReportPayloadAsync(native, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(native, readback);
        Check(native.PayloadRevision == 2 && JsonDocument.Parse(readback.FieldValuesJson!).RootElement.GetProperty("total").GetDecimal() == 30,
            "v2 Apply writes native payload with verified hash and one revision increment");
        var replay = await Post($"aggregate-v2/instances/{instanceId}/apply", apply, fixture.Actor);
        Check(replay.GetProperty("replayed").GetBoolean() && (await db.WorkAssignmentReports.Find(r => r.Id == native.Id).SingleAsync(ct)).PayloadRevision == 2,
            "HTTP lost-response retry returns receipt without duplicate native write");
        before = await Snapshot(db, fixture, ct);
        await Post($"aggregate-v2/instances/{instanceId}/apply", apply, fixture.Outsider, HttpStatusCode.NotFound);
        Check(before == await Snapshot(db, fixture, ct), "unauthorized receipt replay performs no writes");
        change = change with { ExpectedRevision = 2 };
        preview = await Post($"aggregate-v2/instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change), fixture.Actor);
        var changedSource = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Source.Id).SingleAsync(ct);
        var updatedPayload = await payload.SaveReportPayloadAsync(changedSource, "[]", "{\"n\":31}", null, null, fixture.Source.AssigneeUserId, DateTime.UtcNow, ct);
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == fixture.Source.Id, Builders<tdtd_be.Models.WorkAssignmentReport>.Update
            .Set(r => r.PayloadRevision, updatedPayload.PayloadRevision).Set(r => r.PayloadHash, updatedPayload.PayloadHash)
            .Set(r => r.PayloadSizeBytes, updatedPayload.PayloadSizeBytes).Set(r => r.PayloadStatus, updatedPayload.PayloadStatus), cancellationToken: ct);
        before = await Snapshot(db, fixture, ct);
        denied = await Post($"aggregate-v2/instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + "-stale", context, change,
            preview.GetProperty("token").GetString()!), fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_CONFIRMATION_STALE" && before == await Snapshot(db, fixture, ct),
            "source changed after preview rejects stale confirmation without writes");
        preview = await Post($"aggregate-v2/instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change), fixture.Actor);
        apply = new(run + "-apply31", context, change, preview.GetProperty("token").GetString()!);
        await Post($"aggregate-v2/instances/{instanceId}/apply", apply, fixture.Actor);
        native = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
        readback = await payload.LoadReportPayloadAsync(native, ct);
        Check(native.PayloadRevision == 3 && JsonDocument.Parse(readback.FieldValuesJson!).RootElement.GetProperty("total").GetDecimal() == 31,
            "fresh preview applies changed source with native revision 3");
        change = change with { ExpectedRevision = 3 };
        preview = await Post($"aggregate-v2/instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change), fixture.Actor);
        await db.WorkTemplateAssignees.UpdateOneAsync(b => b.Id == fixture.Binding.Id,
            Builders<tdtd_be.Models.WorkTemplateAssignee>.Update.Set(b => b.AssigneeUserId, fixture.Outsider), cancellationToken: ct);
        before = await Snapshot(db, fixture, ct);
        denied = await Post($"aggregate-v2/instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + "-revoked", context, change,
            preview.GetProperty("token").GetString()!), fixture.Actor, HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_CONTEXT_UNAVAILABLE" && before == await Snapshot(db, fixture, ct),
            "revoked assignment binding rejects a previously authorized confirmation without writes");
        await db.WorkTemplateAssignees.UpdateOneAsync(b => b.Id == fixture.Binding.Id,
            Builders<tdtd_be.Models.WorkTemplateAssignee>.Update.Set(b => b.AssigneeUserId, fixture.Actor), cancellationToken: ct);
        var submission = await Post($"aggregate-v2/reports/{fixture.Report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context), fixture.Actor);
        await LifecycleChecks.Run(db, runner, builder.Configuration, fixture, run, instanceId, submission.GetProperty("token").GetString()!, ct);
        before = await Snapshot(db, fixture, ct);
        builder.Configuration["AggregateMapping:V2Enabled"] = "false";
        denied = await Post($"aggregate-v2/instances/{instanceId}/apply", apply, fixture.Actor, HttpStatusCode.Conflict);
        Check(denied.GetProperty("code").GetString() == "AGG_ACTIVATION_REQUIRED" && before == await Snapshot(db, fixture, ct),
            "disabled v2 rejects even receipt replay without writes");
        builder.Configuration["AggregateMapping:V2Enabled"] = "true";
        await PeriodicChecks.Run(db, runner, builder.Configuration, run, recipe, Post, ct);
        await TableChecks.Run(db, runner, run, Post, ct);
        await CalendarAccessChecks.Run(db, runner, run, recipe, fixture.Source.Id, Post, ct);
        await RecoveryChecks.Run(db, runner, run, recipe, Post, Put, ct);
        await BatchReadChecks.Run(db, run, ct);
        await MissingSlotChecks.Run(db, runner, builder.Configuration, run, recipe, Post, ct);
        await PreviewJobChecks.Run(db, runner, builder.Configuration, run, recipe, Post, ct);
        await FullHostChecks.Run(db, fixture, instanceId, run, ct);
        await app.StopAsync(ct);
    }
    static async Task<string> Snapshot(MongoDbContext db, RuntimeFixture fixture, CancellationToken ct)
    {
        var rows = new List<string>();
        foreach (var name in AggregateCollections.All)
            rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", fixture.WorkId)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
        var guardIds = new[] { "REPORT:" + fixture.Report.Id, "REPORT:" + fixture.Source.Id, "SLOT:" + fixture.Binding.Id + ":ONCE" };
        rows.AddRange((await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks).Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(guardIds))))
            .Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
        foreach (var name in new[] { db.WorkAssignments.CollectionNamespace.CollectionName, db.WorkTemplateAssignees.CollectionNamespace.CollectionName, db.WorkReportPeriods.CollectionNamespace.CollectionName })
            rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", ObjectId.Parse(fixture.WorkId))).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
        foreach (var entry in new[] { (db.Users.CollectionNamespace.CollectionName, new[] { fixture.Actor, fixture.Outsider, fixture.Source.AssigneeUserId }),
            (db.Works.CollectionNamespace.CollectionName, new[] { fixture.WorkId }),
            (db.DynamicFormTemplates.CollectionNamespace.CollectionName, new[] { fixture.SourceForm.Id, fixture.TargetForm.Id }) })
            rows.AddRange((await db.Db.GetCollection<BsonDocument>(entry.Item1).Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(entry.Item2.Select(ObjectId.Parse)))))
                .Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
        foreach (var name in new[] { db.WorkAssignmentReports.CollectionNamespace.CollectionName, db.WorkReportPayloads.CollectionNamespace.CollectionName, db.WorkReportTableValues.CollectionNamespace.CollectionName })
        {
            var filter = name == db.WorkAssignmentReports.CollectionNamespace.CollectionName ? new BsonDocument("workId", ObjectId.Parse(fixture.WorkId))
                : new BsonDocument("reportId", new BsonDocument("$in", new BsonArray(new[] { ObjectId.Parse(fixture.Report.Id), ObjectId.Parse(fixture.Source.Id) })));
            rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
        }
        return string.Join("\n", rows);
    }
}
