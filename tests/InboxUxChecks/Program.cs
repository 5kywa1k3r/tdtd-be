using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Middleware;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Models;
using tdtd_be.Hubs;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.Works;
using tdtd_be.Services.StatisticsConfiguration;

var dbName = "inbox_ux_checks_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var ctx = new MongoDbContext(Options.Create(new MongoOptions {
    ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = dbName }));
Console.WriteLine("FIXTURE_DATABASE " + dbName);
string Id() => ObjectId.GenerateNewId().ToString();
int count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> run, string label) {
    try { await run(); } catch (AppException) { Check(true, label); return; }
    throw new Exception("Accepted: " + label);
}
var issuer = Id(); var receiver = Id(); var foreign = Id(); var formOwner = Id();
await ctx.Users.InsertManyAsync(new[] {
    new AppUser { Id = issuer, Username = "issuer", FullName = "Người giao", PasswordHash = "fixture" },
    new AppUser { Id = receiver, Username = "receiver", FullName = "Người nhận", PasswordHash = "fixture" },
    new AppUser { Id = foreign, Username = "foreign", FullName = "Bên khác", PasswordHash = "fixture" },
    new AppUser { Id = formOwner, Username = "form-owner", FullName = "Chủ biểu mẫu", PasswordHash = "fixture" } });
var work = new Work { Id = Id(), Name = "Nhiệm vụ nguồn", CreatedByUserId = issuer };
await ctx.Works.InsertOneAsync(work);
var sections = """[{"id":"main","title":"Nội dung","order":0}]""";
var fields = """[{"id":"items","key":"items","label":"Danh sách","type":"stringList","sectionId":"main","order":0},{"id":"files","key":"files","label":"Tệp đính kèm","type":"evidence","sectionId":"main","order":1}]""";
var nativeList = """[{"id":"staff-list","sectionId":"main","name":"Danh sách cán bộ","order":0,"layout":"vertical","fields":[{"id":"full-name","name":"Họ tên","order":0}],"rows":[],"typeConfig":{"version":1,"sequence":1,"rules":[{"target":{"scope":"column","fieldId":"full-name"},"spec":{"type":"plainText","required":true,"maxLength":200},"order":1}]},"statisticTargets":[],"presentation":{"kind":"LIST","itemLabel":"Cán bộ","addLabel":"Thêm cán bộ","summaryFieldIds":["full-name"]},"itemConstraints":{"minItems":0,"maxItems":200}}]""";
var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(1, sections, fields, "[]", 2, nativeList);
var source = new DynamicFormTemplate { Id = Id(), Code = "FIXTURE-SOURCE", Name = "Biểu mẫu nguồn khác tên việc",
    CreatedByUserId = formOwner, CreatedByUsername = "form-owner", IsActive = true, IsPublished = true, VersionNo = 1,
    SectionsJson = sections, FieldsJson = fields, BlocksJson = "[]", SchemaVersion = 1, NativeTablesVersion = 2, TablesJson = nativeList,
    PublishedSchemaSnapshotJson = snapshot.Json, PublishedSchemaHash = snapshot.Sha256 };
source.FamilyId = source.Id;
await ctx.DynamicFormTemplates.InsertOneAsync(source);
await ctx.DynamicFormTemplates.Indexes.CreateOneAsync(new CreateIndexModel<DynamicFormTemplate>(
    Builders<DynamicFormTemplate>.IndexKeys.Ascending(x => x.Code), new CreateIndexOptions { Unique = true }));
await ctx.DynamicFormCloneRequests.Indexes.CreateOneAsync(new CreateIndexModel<DynamicFormCloneRequest>(
    Builders<DynamicFormCloneRequest>.IndexKeys.Ascending(x => x.WorkAssignmentId).Ascending(x => x.DynamicFormTemplateId).Ascending(x => x.RequesterUserId),
    new CreateIndexOptions<DynamicFormCloneRequest> { Unique = true, PartialFilterExpression = Builders<DynamicFormCloneRequest>.Filter.Eq(x => x.Status, "PENDING") & Builders<DynamicFormCloneRequest>.Filter.Eq(x => x.IsDeleted, false) }));
var a = new WorkAssignment { Id = Id(), WorkId = work.Id, Code = "WA000001", Name = "Tên phần việc",
    CreatedByUserId = issuer, CurrentReviewerUserId = issuer, IsActive = true, DynamicFormTemplateId = source.Id,
    DynamicFormTemplateName = source.Name, DynamicFormVersionNo = 1, DynamicFormFamilyId = source.Id, DynamicFormSchemaHash = snapshot.Sha256,
    Assignees = new() { new() { UserId = receiver, FullName = "Người nhận" }, new() { UserId = foreign, FullName = "Bên khác" } } };
await ctx.WorkAssignments.InsertOneAsync(a);
var p = new WorkReportPeriod { Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkTemplateAssigneeId = Id(),
    AssigneeUserId = receiver, IsActive = true, PeriodKey = "20261006", PeriodInstanceKey = Id(),
    DynamicFormTemplateId = source.Id, DynamicFormFamilyId = source.Id, DynamicFormVersionNo = 1, DynamicFormSchemaHash = snapshot.Sha256 };
var otherPeriod = new WorkReportPeriod { Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkTemplateAssigneeId = Id(),
    AssigneeUserId = foreign, IsActive = true, PeriodKey = "20261006", PeriodInstanceKey = Id() };
await ctx.WorkReportPeriods.InsertManyAsync(new[] { p, otherPeriod });
var report = new WorkAssignmentReport { Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkReportPeriodId = p.Id,
    AssigneeUserId = receiver, PeriodKey = p.PeriodKey, PeriodInstanceKey = p.PeriodInstanceKey, IsActive = true, IsCurrent = true,
    DynamicFormTemplateId = source.Id, DynamicFormFamilyId = source.Id, DynamicFormVersionNo = 1, DynamicFormSchemaHash = snapshot.Sha256,
    FieldValuesJson = """{"items":["Giá trị nguồn"],"files":[{"id":"fixture-evidence","name":"tep-kiem-thu.txt"}]}""",
    TableValuesJson = """{"staff-list":{"records":[{"id":"row1","values":{"full-name":"Người kiểm thử"}}]}}""" };
await ctx.WorkAssignmentReports.InsertOneAsync(report);
var notifications = new NotificationService(ctx, Proxy.For<IHubContext<NotificationsHub>>(), NullLogger<NotificationService>.Instance);
var inbox = new WorkInboxService(ctx, notifications, Proxy.For<IWorkPermissionService>(), new ConfigurationBuilder().Build());
var requests = new DynamicFormCloneRequestService(ctx, notifications);
var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
void Actor(string id) => http.HttpContext!.Items[MeAccessor.MeItemKey] = new MeResponse(id, id == issuer ? "issuer" : "receiver",
    "Người kiểm", [], "", null, null, null, [], null, false);
var forms = new DynamicFormService(ctx, new MeAccessor(http), Proxy.For<IDynamicFormStatisticConfigCommandService>(), Proxy.For<ILabelEnumCatalogService>());
async Task<string> Protected() => string.Join("\n", new[] {
    (await ctx.DynamicFormTemplates.Find(x => x.Id == source.Id).FirstAsync()).ToJson(),
    (await ctx.WorkAssignments.Find(x => x.Id == a.Id).FirstAsync()).ToJson(),
    (await ctx.WorkReportPeriods.Find(x => x.WorkAssignmentId == a.Id).SortBy(x => x.Id).ToListAsync()).ToJson(),
    (await ctx.WorkAssignmentReports.Find(x => x.WorkAssignmentId == a.Id).SortBy(x => x.Id).ToListAsync()).ToJson() });
var protectedBefore = await Protected();
var ownDetail = await inbox.AssignmentDetailAsync(a.Id, receiver, default);
Check(ownDetail is { IsRecipient: true, CanRequestClone: true, CloneStatus: "NOT_REQUESTED" }
    && ownDetail.Issuer?.FullName == "Người giao" && ownDetail.FormVersion == 1, "receiver detail uses issuer, form pin, actual clone eligibility");
Check(await inbox.AssignmentDetailAsync(a.Id, Id(), default) is null, "foreign user cannot read assignment popup");
Check((await inbox.AssignmentDetailAsync(a.Id, issuer, default)) is { IsRecipient: false, CanRequestClone: false }, "reviewer has context but no receiver clone action");
await ctx.DynamicFormTemplates.UpdateOneAsync(x => x.Id == source.Id, Builders<DynamicFormTemplate>.Update.Set(x => x.CreatedByUserId, receiver));
Check((await inbox.AssignmentDetailAsync(a.Id, receiver, default)) is { CanClone: true, CanRequestClone: false }, "actual form owner recipient is never asked to request permission");
await Reject(() => requests.CreateAsync(a.Id, new(null), receiver, default), "command rejects pointless request by actual form owner");
await ctx.DynamicFormTemplates.UpdateOneAsync(x => x.Id == source.Id, Builders<DynamicFormTemplate>.Update.Set(x => x.CreatedByUserId, formOwner));
protectedBefore = await Protected();
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == a.Id, Builders<WorkAssignment>.Update.Set(x => x.CurrentReviewerUserId, foreign));
Check(await inbox.AssignmentDetailAsync(a.Id, issuer, default) is null, "original issuer loses popup review context after reviewer handover");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == a.Id, Builders<WorkAssignment>.Update.Set(x => x.CurrentReviewerUserId, issuer));
protectedBefore = await Protected();
var ownPeriods = await inbox.SearchAsync(new() { AssignmentId = a.Id, Function = "REPORT" }, receiver, default);
Check(ownPeriods.Items.Count == 1 && ownPeriods.Items[0].Target.AssigneeUserId == receiver, "shared assignment never exposes other recipient report choices");
Check((await inbox.SummaryAsync(receiver, default, Id())).Total == 0, "work-filtered summary does not show global counts");
Actor(receiver);
await Reject(() => forms.CloneAsync(source.Id, new("FIXTURE-DENIED", "Chưa được phép"), default), "runtime read/assignment membership does not grant clone");
var created = await requests.CreateAsync(a.Id, new("Dùng bản sao độc lập"), receiver, default);
Check(await Protected() == protectedBefore, "creating request leaves source, version, assignment, report data byte-equivalent");
Check((await inbox.AssignmentDetailAsync(a.Id, receiver, default)) is { CloneStatus: "PENDING", CanRequestClone: false }, "pending request readback prevents repeated UI request");
await Reject(() => requests.CreateAsync(a.Id, new(null), receiver, default), "repeat pending request rejected");
await Reject(() => requests.ApproveAsync(created.Id, new(null), receiver, default), "requester cannot approve own request");
await requests.RejectAsync(created.Id, new("Chưa phù hợp"), issuer, default);
Check((await inbox.AssignmentDetailAsync(a.Id, receiver, default)) is { CloneStatus: "REJECTED", CanRequestClone: true }, "rejected request tracked without granting copy");
await Reject(() => forms.CloneAsync(source.Id, new("FIXTURE-REJECTED", "Bản bị từ chối"), default), "rejection never grants clone");
var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ => {
    try { await requests.CreateAsync(a.Id, new(null), receiver, default); return true; } catch (AppException) { return false; } }));
Check(concurrent.Count(x => x) == 1, "five fast concurrent requests yield one pending request and domain errors");
var pending = await ctx.DynamicFormCloneRequests.Find(x => x.RequesterUserId == receiver && x.Status == "PENDING").FirstAsync();
var decisions = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ => {
    try { await requests.ApproveAsync(pending.Id, new(null), issuer, default); return true; } catch (AppException) { return false; } }));
Check(decisions.Count(x => x) == 1, "concurrent approval commits one decision");
Check(await Protected() == protectedBefore, "original assignment issuer approves another owner's form without changing protected source data");
Check((await inbox.AssignmentDetailAsync(a.Id, receiver, default)) is { CloneStatus: "GRANTED", CanClone: true, CanRequestClone: false }, "approved requester can copy without asking again");
await Reject(() => requests.CreateAsync(a.Id, new(null), receiver, default), "existing grant cannot create needless new request");
var clone = await forms.CloneAsync(source.Id, new("FIXTURE-COPY", "Bản sao riêng"), default);
Check(clone.Id != source.Id && clone.FamilyId == clone.Id && clone.CreatedByUserId == receiver && !clone.IsPublished
    && clone.VersionNo == 1 && clone.ClonedFromVersionId == source.Id, "clone owns new ID/family, pinned source lineage, independent draft");
Check(clone.FieldsJson == fields && clone.SectionsJson == sections, "string-list and evidence field IDs/references retained in independent schema");
var cloneStored = await ctx.DynamicFormTemplates.Find(x => x.Id == clone.Id).FirstAsync();
Check(cloneStored.NativeTablesVersion == 2 && cloneStored.TablesJson == nativeList
    && clone.Schema?.Tables?.Single().Presentation?.Kind == "LIST", "native list presentation, item constraints and field references survive copy/open");
Check(await Protected() == protectedBefore, "copy leaves source/version/assignment/reports unchanged");
var opened = await forms.GetByIdAsync(clone.Id, default);
Check(opened.Actions.CanUpdate && opened.Actions.CanClone, "requester can open owned clone");
var saved = await forms.UpdateAsync(clone.Id, new("Bản sao đã sửa", null, [], clone.SchemaVersion,
    null, null, null, null, Schema: clone.Schema, ExpectedRevision: clone.Revision), default);
Check(saved.Name == "Bản sao đã sửa" && saved.Revision > clone.Revision, "clone saves through actual update service");
Check(saved.Schema?.Tables?.Single().Presentation?.Kind == "LIST" && saved.Schema.Fields?.Count == 2, "saving typed native-list clone preserves list and evidence fields");
Check(await Protected() == protectedBefore, "saving copy leaves protected source data unchanged");
await Reject(() => forms.UpdateAsync(source.Id, new("Sửa trái quyền", null, [], source.SchemaVersion,
    sections, fields, null, "[]", ExpectedRevision: 1), default), "clone grant does not grant source mutation");
var beforeCount = await ctx.DynamicFormTemplates.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty);
await Reject(() => forms.CloneAsync(source.Id, new("FIXTURE-COPY", "Trùng mã"), default), "failed duplicate-code copy returns domain error");
Check(await ctx.DynamicFormTemplates.CountDocumentsAsync(FilterDefinition<DynamicFormTemplate>.Empty) == beforeCount
    && await Protected() == protectedBefore, "failed copy leaves no partial form or source change");
// Legacy notifications resolve names without replacing their event times or changing cursor order.
var sameTime = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
await notifications.CreateManyAsync(Enumerable.Range(0, 4).Select(i => new NotificationCommand {
    RecipientUserId = receiver, Type = "ASSIGNMENT_ASSIGNED", Title = "Legacy", Body = "WA000001 - FORM-TECH", WorkId = i == 0 ? null : work.Id,
    WorkAssignmentId = a.Id, EventKey = "legacy:" + i, OccurredAtUtc = sameTime }));
var page1 = await inbox.HistoryAsync(new() { PageSize = 2, Types = ["ASSIGNMENT_ASSIGNED"] }, receiver, default);
var page2 = await inbox.HistoryAsync(new() { PageSize = 2, Types = ["ASSIGNMENT_ASSIGNED"], CursorId = page1.NextCursorId,
    CursorOccurredAtUtc = page1.NextCursorOccurredAtUtc }, receiver, default);
Check(page1.Items.Concat(page2.Items).All(x => x.Notification.AssignmentName == a.Name && x.Notification.WorkName == work.Name
    && x.Notification.OccurredAtUtc == sameTime), "legacy name enrichment uses assignment/work names, preserves event instant");
Check(page1.Items.Concat(page2.Items).Select(x => x.Notification.Id).Distinct().Count() == 4
    && page1.Items.Select(x => x.Notification.Id).SequenceEqual(page1.Items.Select(x => x.Notification.Id).OrderDescending()), "same-time seek pages stay stable with unique ID tie break");
var repeat = await inbox.HistoryAsync(new() { PageSize = 2, Types = ["ASSIGNMENT_ASSIGNED"] }, receiver, default);
Check(repeat.Items.Select(x => x.Notification.Id).SequenceEqual(page1.Items.Select(x => x.Notification.Id)), "refresh preserves tied notification order");
var obligations = (await inbox.SummaryAsync(receiver, default)).Total;
await notifications.MarkManyReadAsync(page1.Items.Select(x => x.Notification.Id), receiver);
Check((await inbox.SummaryAsync(receiver, default)).Total == obligations, "reading does not complete work");
await Reject(() => requests.CreateAsync(a.Id, new(null), issuer, default), "issuer who is not recipient never asks own approval");
// Real authenticated controller routes in an ephemeral local host; shared hosts and DB are untouched.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
builder.Services.AddSingleton(ctx); builder.Services.AddSingleton<INotificationService>(notifications);
builder.Services.AddSingleton(inbox); builder.Services.AddSingleton<IDynamicFormCloneRequestService>(requests);
builder.Services.AddScoped<ApiExceptionMiddleware>();
builder.Services.AddControllers().AddApplicationPart(typeof(WorkInboxController).Assembly);
var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
    options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "inbox-ux-fixture",
        ValidateAudience = true, ValidAudience = "inbox-ux-fixture", ValidateLifetime = true, ValidateIssuerSigningKey = true,
        IssuerSigningKey = signingKey, ClockSkew = TimeSpan.Zero };
});
builder.Services.AddAuthorization();
await using var app = builder.Build(); app.UseMiddleware<ApiExceptionMiddleware>(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync();
try {
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    Check((await client.GetAsync("/api/me/work-inbox/assignments/" + a.Id)).StatusCode == HttpStatusCode.Unauthorized, "HTTP popup rejects missing JWT");
    var jwt = new JwtSecurityToken("inbox-ux-fixture", "inbox-ux-fixture", [new Claim(ClaimTypes.NameIdentifier, receiver)],
        expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    var detailResponse = await client.GetAsync("/api/me/work-inbox/assignments/" + a.Id);
    var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
    Check(detailResponse.IsSuccessStatusCode && detailJson.RootElement.GetProperty("isRecipient").GetBoolean()
        && !detailJson.RootElement.TryGetProperty("reports", out _), "HTTP popup returns recipient projection without any report payload");
    Check((await client.GetAsync("/api/me/work-inbox/assignments/" + Id())).StatusCode == HttpStatusCode.NotFound, "HTTP unknown popup gives clear 404");
    Check((await client.GetAsync("/api/me/work-inbox/summary?workId=invalid")).StatusCode == HttpStatusCode.BadRequest, "HTTP rejects invalid scoped summary filter");
    var periodResponse = await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { assignmentId = a.Id, function = "REPORT", pageSize = 10, actorUserId = foreign });
    var periodJson = JsonDocument.Parse(await periodResponse.Content.ReadAsStringAsync());
    Check(periodResponse.IsSuccessStatusCode && periodJson.RootElement.GetProperty("items").EnumerateArray()
        .All(x => x.GetProperty("target").GetProperty("assigneeUserId").GetString() == receiver), "HTTP ignores forged actor and keeps own-period scope");
    var grantResponse = await client.PostAsJsonAsync("/api/work-assignments/" + a.Id + "/dynamic-form-clone-requests", new { reason = "duplicate grant" });
    Check(grantResponse.StatusCode == HttpStatusCode.BadRequest, "HTTP existing grant cannot create unnecessary request");
    var notificationResponse = await client.PostAsJsonAsync("/api/me/work-inbox/notifications/history/search", new { pageSize = 10 });
    var notificationJson = JsonDocument.Parse(await notificationResponse.Content.ReadAsStringAsync());
    Check(notificationResponse.IsSuccessStatusCode && notificationJson.RootElement.GetProperty("items").EnumerateArray()
        .All(x => x.GetProperty("notification").GetProperty("assignmentName").GetString() == a.Name), "HTTP names are enriched for legacy notifications");
    // Eight warm sequential HTTP samples, including body read; diagnostic only, no arbitrary latency assertion.
    Console.WriteLine($"PERF_DATA works={await ctx.Works.CountDocumentsAsync(FilterDefinition<Work>.Empty)} assignments={await ctx.WorkAssignments.CountDocumentsAsync(FilterDefinition<WorkAssignment>.Empty)} periods={await ctx.WorkReportPeriods.CountDocumentsAsync(FilterDefinition<WorkReportPeriod>.Empty)}");
    foreach (var route in new[] { "popup", "list" }) {
        var samples = new List<double>();
        for (var sample = 0; sample < 8; sample++) {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var response = route == "popup"
                ? await client.GetAsync("/api/me/work-inbox/assignments/" + a.Id)
                : await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { function = "REPORT", pageSize = 10 });
            response.EnsureSuccessStatusCode(); await response.Content.ReadAsByteArrayAsync();
            timer.Stop(); samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        Console.WriteLine($"PERF_HTTP {route} samples=8 warm sequential medianMs={(samples[3] + samples[4]) / 2:F2} maxMs={samples[^1]:F2}; private small fixture, not INP or production capacity");
    }
} finally { await app.StopAsync(); }
// Revoked membership cannot retain pending approval or popup/report links.
var foreignRequest = await requests.CreateAsync(a.Id, new(null), foreign, default);
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == a.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, a.Assignees.Where(x => x.UserId != foreign).ToList()));
await Reject(() => requests.ApproveAsync(foreignRequest.Id, new(null), issuer, default), "removed recipient pending request cannot be approved");
Check(!(await inbox.SearchAsync(new() { Function = "REQUEST" }, issuer, default)).Items.Any(x => x.Target.RequestId == foreignRequest.Id),
    "removed recipient request is no longer an approver obligation");
Check(await inbox.AssignmentDetailAsync(a.Id, foreign, default) is null && await inbox.ItemAsync("report:" + otherPeriod.Id, foreign, default) is null,
    "revoked recipient cannot reopen cached popup or report");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == a.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, a.Assignees));
await ctx.DynamicFormTemplates.UpdateOneAsync(x => x.Id == source.Id, Builders<DynamicFormTemplate>.Update.Set(x => x.IsDeleted, true));
await Reject(() => requests.ApproveAsync(foreignRequest.Id, new(null), issuer, default), "deleted source pending approval fails closed");
Check(!(await inbox.SearchAsync(new() { Function = "REQUEST" }, issuer, default)).Items.Any(x => x.Target.RequestId == foreignRequest.Id)
    && (await inbox.ItemAsync("clone:" + foreignRequest.Id, issuer, default)) is { RequiresAction: false },
    "deleted source removes request obligation and leaves read-only historical context");
Actor(receiver);
await Reject(() => forms.CloneAsync(source.Id, new("FIXTURE-DELETED", "Nguồn đã xóa"), default), "deleted source cannot be copied despite old grant");
Check((await inbox.AssignmentDetailAsync(a.Id, receiver, default)) is { CloneStatus: "UNAVAILABLE", CanRequestClone: false, CanClone: false }, "unavailable source has explicit derived UI state");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == a.Id, Builders<WorkAssignment>.Update.Set(x => x.IsActive, false));
Check(await inbox.AssignmentDetailAsync(a.Id, receiver, default) is null, "inactive assignment popup fails closed");
Console.WriteLine($"PASS {count} checks; isolated Mongo source services. No shared UAT writes, no browser acceptance claim.");

public class Proxy : DispatchProxy {
    public static T For<T>() where T : class => Create<T, Proxy>();
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        var type = method!.ReturnType;
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsInterface) return typeof(Proxy).GetMethod(nameof(For))!.MakeGenericMethod(type).Invoke(null, null);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
            var inner = type.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
