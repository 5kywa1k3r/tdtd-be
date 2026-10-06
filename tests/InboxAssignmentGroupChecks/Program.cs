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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Hubs;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.Works;

var dbName = "inbox_assignment_group_checks_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var ctx = new MongoDbContext(Options.Create(new MongoOptions {
    ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = dbName }));
Console.WriteLine("FIXTURE_DATABASE " + dbName);
string Id() => ObjectId.GenerateNewId().ToString();
int count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
var actor = Id(); var other = Id();
var now = DateTime.UtcNow; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
var work = new Work { Id = Id(), Name = "Nhiệm vụ fixture grouping", CreatedByUserId = actor };
await ctx.Works.InsertOneAsync(work);
WorkAssignment Assignment(string name, string reviewer) => new() { Id = Id(), WorkId = work.Id, Code = "FIXTURE", Name = name,
    IsActive = true, CreatedByUserId = reviewer, CurrentReviewerUserId = reviewer, DynamicFormTemplateId = Id() };
WorkReportPeriod Period(WorkAssignment a, string assignee, DateTime? due, string key, WorkReportPeriodStatus status) => new() {
    Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkTemplateAssigneeId = Id(), AssigneeUserId = assignee,
    DueAtUtc = due, PeriodKey = key, PeriodInstanceKey = Id(), IsActive = true, Status = status,
    LastSubmittedAtUtc = now, ReportTitle = "Báo cáo " + a.Name };
WorkAssignmentReport Report(WorkReportPeriod p, WorkAssignmentReportStatus status) => new() {
    Id = Id(), WorkId = work.Id, WorkAssignmentId = p.WorkAssignmentId, WorkReportPeriodId = p.Id,
    AssigneeUserId = p.AssigneeUserId, PeriodKey = p.PeriodKey, PeriodInstanceKey = p.PeriodInstanceKey,
    IsActive = true, IsCurrent = true, Status = status };
var main = Assignment("Đầu việc 200 đơn vị [x].*", actor);
var periods = new List<WorkReportPeriod>(); var reports = new List<WorkAssignmentReport>(); var logs = new List<WorkAssignmentReportLog>();
for (var i = 0; i < 200; i++) {
    var assignee = Id(); main.Assignees.Add(new() { UserId = assignee, FullName = "Đơn vị " + i });
    var due = i < 80 ? now.AddDays(-2) : i < 140 ? now.AddHours(12) : now.AddDays(3);
    var p = Period(main, assignee, due, "202610" + (i % 4), i < 80 ? WorkReportPeriodStatus.OverdueSubmitted : WorkReportPeriodStatus.Submitted);
    var r = Report(p, WorkAssignmentReportStatus.Submitted); p.CurrentReportId = r.Id; periods.Add(p); reports.Add(r);
    if (i < 10) logs.Add(new() { Id = Id(), WorkId = work.Id, WorkAssignmentId = main.Id, WorkReportPeriodId = p.Id,
        WorkAssignmentReportId = r.Id, Action = "RETURN", ActionByUserId = actor, ActionAtUtc = now.AddDays(-3) });
}
await ctx.WorkAssignments.InsertOneAsync(main); await ctx.WorkReportPeriods.InsertManyAsync(periods);
await ctx.WorkAssignmentReports.InsertManyAsync(reports); await ctx.WorkAssignmentReportLogs.InsertManyAsync(logs);
var ties = new List<WorkAssignment>();
for (var i = 0; i < 3; i++) {
    var a = Assignment("Tên đầu việc trùng nhau", actor); a.Assignees = [new() { UserId = other }]; ties.Add(a);
    var p = Period(a, other, now.AddDays(3), "20261006", WorkReportPeriodStatus.Submitted);
    var r = Report(p, WorkAssignmentReportStatus.Submitted); p.CurrentReportId = r.Id;
    await ctx.WorkAssignments.InsertOneAsync(a); await ctx.WorkReportPeriods.InsertOneAsync(p); await ctx.WorkAssignmentReports.InsertOneAsync(r);
}
var foreign = Assignment("Đầu việc của reviewer khác", other); foreign.Assignees = [new() { UserId = other }];
var foreignPeriod = Period(foreign, other, now.AddDays(-2), "20261006", WorkReportPeriodStatus.Submitted);
var foreignReport = Report(foreignPeriod, WorkAssignmentReportStatus.Submitted); foreignPeriod.CurrentReportId = foreignReport.Id;
await ctx.WorkAssignments.InsertOneAsync(foreign); await ctx.WorkReportPeriods.InsertOneAsync(foreignPeriod); await ctx.WorkAssignmentReports.InsertOneAsync(foreignReport);
var own = Assignment("Đầu việc báo cáo chung", other); own.Assignees = [new() { UserId = actor }, new() { UserId = other }];
await ctx.WorkAssignments.InsertOneAsync(own);
var ownPeriods = new List<WorkReportPeriod>();
for (var i = 0; i < 3; i++) ownPeriods.Add(Period(own, actor, now.AddDays(4), "OWN" + i, WorkReportPeriodStatus.Pending));
var submitted = Period(own, actor, now.AddDays(-10), "OWN-SENT", WorkReportPeriodStatus.Submitted);
var submittedReport = Report(submitted, WorkAssignmentReportStatus.Submitted); submitted.CurrentReportId = submittedReport.Id;
ownPeriods.Add(submitted);
var invalid = Period(own, actor, now.AddDays(-1), "INVALID", WorkReportPeriodStatus.Pending); invalid.CurrentReportId = reports[0].Id;
ownPeriods.Add(invalid);
for (var i = 0; i < 7; i++) ownPeriods.Add(Period(own, other, now.AddDays(-1), "SIBLING" + i, WorkReportPeriodStatus.Pending));
await ctx.WorkReportPeriods.InsertManyAsync(ownPeriods); await ctx.WorkAssignmentReports.InsertOneAsync(submittedReport);
var notifications = new NotificationService(ctx, Proxy.For<IHubContext<NotificationsHub>>(), NullLogger<NotificationService>.Instance);
var inbox = new WorkInboxService(ctx, notifications, Proxy.For<IWorkPermissionService>(), new ConfigurationBuilder().Build());
var allReviews = await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", PageSize = 50 }, actor, default);
var mainGroup = allReviews.Items.Single(x => x.AssignmentId == main.Id);
Check(allReviews.Items.Count == 4 && !allReviews.HasMore, "203 review obligations produce four assignment cards not203cards");
Check(mainGroup.Total == 200 && mainGroup.PendingCount == 200 && mainGroup.HandledCount == 0, "full200members counted before any card page limit");
Check(mainGroup.AssigneeCount == 200 && mainGroup.PeriodCount == 4, "group counts distinct assignees and period keys");
Check(mainGroup.OverdueCount == 80 && mainGroup.DueSoonCount == 60 && mainGroup.ResubmissionCount == 10, "current urgency and resubmission counts cover all group members");
Check(mainGroup.Priority == 0 && mainGroup.DueAtUtc == now.AddDays(-2), "group picks most urgent rank and earliest pending deadline");
Check(allReviews.Items.Count(x => x.AssignmentName == "Tên đầu việc trùng nhau") == 3, "same display names remain different assignment groups");
Check(!allReviews.Items.Any(x => x.AssignmentId == foreign.Id), "foreign current reviewer never contributes group or counts");
var literal = await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", SearchText = "[x].*", PageSize = 1 }, actor, default);
Check(literal.Items.Single().Total == 200 && !literal.HasMore, "literal search and page size one retain full member count");
var late = await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", State = "OVERDUE" }, actor, default);
Check(late.Items.Single().Total == 80 && late.Items[0].OverdueCount == 80 && late.Items[0].DueSoonCount == 0, "urgency filter scopes every group counter");
var dated = await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", DueFromUtc = now, DueBeforeUtc = now.AddDays(3) }, actor, default);
Check(dated.Items.Single().Total == 60 && dated.Items[0].DueSoonCount == 60 && dated.Items[0].AssigneeCount == 60, "date filter inclusive start exclusive end applies before group and distinct counts");
var memberPage = await inbox.SearchAsync(new() { Function = "REVIEW", AssignmentId = main.Id, PageSize = 50 }, actor, default);
Check(memberPage.Items.Count == 50 && memberPage.HasMore && mainGroup.Total == 200, "member endpoint is paged independently and group count is not truncated to50");
var memberIds = new List<string>(); var memberRequest = new WorkInboxSearchRequest { Function = "REVIEW", AssignmentId = main.Id, PageSize = 50 };
do { var page = await inbox.SearchAsync(memberRequest, actor, default); memberIds.AddRange(page.Items.Select(x => x.Id));
    if (!page.HasMore) break; memberRequest.CursorId = page.NextCursorId; memberRequest.CursorPriority = page.NextCursorPriority; memberRequest.CursorDueAtUtc = page.NextCursorDueAtUtc;
} while (true);
Check(memberIds.Count == 200 && memberIds.Distinct().Count() == 200, "dialog can seek all200members through existing items endpoint without duplicates");
var groupIds = new List<string>(); var groupRequest = new WorkInboxSearchRequest { Function = "REVIEW", PageSize = 1 };
do { var page = await inbox.AssignmentGroupsAsync(groupRequest, actor, default); groupIds.AddRange(page.Items.Select(x => x.AssignmentId));
    if (!page.HasMore) break; groupRequest.CursorId = page.NextCursorId; groupRequest.CursorPriority = page.NextCursorPriority; groupRequest.CursorDueAtUtc = page.NextCursorDueAtUtc;
} while (true);
Check(groupIds.Count == 4 && groupIds.Distinct().Count() == 4 && groupIds[0] == main.Id, "assignment cursor pages stable priority date id groups without duplicate or skip");
Check(groupIds.Skip(1).SequenceEqual(ties.Select(x => x.Id).OrderBy(x => x)), "equal priority date group ties use assignment id ordering");
Check((await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", PageSize = 1, PageNumber = 4 }, actor, default)).Items.Single().AssignmentId == groupIds[3],
    "assignment card page number jumps directly to fourth sorted group");
Check((await inbox.SearchAsync(new() { Function = "REVIEW", AssignmentId = main.Id, PageSize = 50, PageNumber = 4 }, actor, default)).Items.Count == 50,
    "dialog member page number jumps directly to fourth page independently of card pagination");
var ownGroup = (await inbox.AssignmentGroupsAsync(new() { Function = "REPORT" }, actor, default)).Items.Single();
Check(ownGroup.AssignmentId == own.Id && ownGroup.Total == 3 && ownGroup.AssigneeCount == 1 && ownGroup.PeriodCount == 3,
    "report group counts actor own obligations only and excludes sibling periods plus foreign report pointer");
var ownBoth = (await inbox.AssignmentGroupsAsync(new() { Function = "REPORT", Handled = null }, actor, default)).Items.Single();
Check(ownBoth.Total == 4 && ownBoth.PendingCount == 3 && ownBoth.HandledCount == 1 && ownBoth.PeriodCount == 4,
    "combined report group counts real submitted writing without foreign pointer handled leak");
Check(ownBoth.DueAtUtc == now.AddDays(4), "combined deadline favors current pending due over old handled deadline");
var ownHandled = (await inbox.AssignmentGroupsAsync(new() { Function = "REPORT", Handled = true }, actor, default)).Items.Single();
Check(ownHandled.Total == 1 && ownHandled.PendingCount == 0 && ownHandled.HandledCount == 1 && ownHandled.DueAtUtc == now.AddDays(-10),
    "handled only group uses sensible earliest handled fallback deadline");
Check((await inbox.SummaryAsync(actor, default)).ByFunction["REVIEW"] == 203, "summary preserves obligation count contract independent of card grouping");
var legacyGroups = JsonSerializer.SerializeToElement(await inbox.GroupsAsync(new() { Function = "REVIEW" }, actor, default));
Check(legacyGroups.GetProperty("Items").GetArrayLength() == 1 && legacyGroups.GetProperty("Items")[0].GetProperty("Total").GetInt64() == 203,
    "legacy work group endpoint preserves one work with obligation total");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(ctx); builder.Services.AddSingleton<tdtd_be.Services.Notifications.INotificationService>(notifications); builder.Services.AddSingleton(inbox);
builder.Services.AddControllers().AddApplicationPart(typeof(WorkInboxController).Assembly);
var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidIssuer = "assignment-group-fixture", ValidateAudience = true, ValidAudience = "assignment-group-fixture",
    ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey, ClockSkew = TimeSpan.Zero });
builder.Services.AddAuthorization(); await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
try {
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    var path = "/api/me/work-inbox/assignment-groups/search";
    Check((await client.PostAsJsonAsync(path, new { function = "REVIEW" })).StatusCode == HttpStatusCode.Unauthorized, "new HTTP group endpoint rejects missing JWT");
    var token = new JwtSecurityToken("assignment-group-fixture", "assignment-group-fixture", [new Claim(ClaimTypes.NameIdentifier, actor)],
        expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: new(signingKey, SecurityAlgorithms.HmacSha256));
    client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    var response = await client.PostAsJsonAsync(path, new { function = "REVIEW", assignmentId = main.Id, pageSize = 1, actorUserId = other });
    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(response.IsSuccessStatusCode && json.RootElement.GetProperty("items")[0].GetProperty("total").GetInt64() == 200,
        "HTTP JWT actor overrides forged actor and returns full200member group");
    Check(!json.RootElement.GetProperty("items")[0].TryGetProperty("items", out _) && !json.RootElement.GetProperty("items")[0].TryGetProperty("target", out _)
        && !json.RootElement.GetProperty("items")[0].TryGetProperty("assignees", out _), "HTTP card response exposes no raw member target or distinct-id arrays");
    foreach (var payload in new object[] { new { function = "ASSIGNMENT" }, new { pageSize = 20 }, new { function = "REVIEW", cursorId = main.Id },
        new { function = "REVIEW", cursorId = "review:" + main.Id, cursorPriority = 0, cursorDueAtUtc = now }, new { function = "REVIEW", state = "BAD" } })
        Check((await client.PostAsJsonAsync(path, payload)).StatusCode == HttpStatusCode.BadRequest, "HTTP group validates function full triad object id and filter scope");
    var first = await client.PostAsJsonAsync(path, new { function = "REVIEW", pageSize = 1 });
    var firstPage = (await first.Content.ReadFromJsonAsync<WorkInboxAssignmentGroupPage>())!;
    var second = await client.PostAsJsonAsync(path, new { function = "REVIEW", pageSize = 1, cursorId = firstPage.NextCursorId,
        cursorPriority = firstPage.NextCursorPriority, cursorDueAtUtc = firstPage.NextCursorDueAtUtc });
    Check(firstPage.Items.Single().AssignmentId == main.Id && (await second.Content.ReadFromJsonAsync<WorkInboxAssignmentGroupPage>())!.Items.Single().AssignmentId != main.Id,
        "HTTP group seek consumes assignment id cursor rather than arbitrary member id");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { function = "REPORT", assignmentId = own.Id })).IsSuccessStatusCode,
        "existing HTTP item endpoint remains available for filtered dialog members");
    var jumpedPage = await client.PostAsJsonAsync(path, new { function = "REVIEW", pageSize = 1, pageNumber = 4 });
    Check(jumpedPage.IsSuccessStatusCode && (await jumpedPage.Content.ReadFromJsonAsync<WorkInboxAssignmentGroupPage>())!.Items.Single().AssignmentId == groupIds[3],
        "HTTP assignment group supports real page number jump");
} finally { await app.StopAsync(); }
for (var i = 0; i < 10; i++) {
    await ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == reports[i].Id, Builders<WorkAssignmentReport>.Update.Set(x => x.Status, WorkAssignmentReportStatus.Approved));
    await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == periods[i].Id, Builders<WorkReportPeriod>.Update.Set(x => x.Status, WorkReportPeriodStatus.Approved));
}
var mixed = (await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", AssignmentId = main.Id, Handled = null }, actor, default)).Items.Single();
Check(mixed.Total == 200 && mixed.PendingCount == 190 && mixed.HandledCount == 10 && mixed.OverdueCount == 70 && mixed.ResubmissionCount == 10,
    "mixed review group counts pending handled urgency and resubmissions from current real statuses");
Check((await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", AssignmentId = main.Id, Handled = true }, actor, default)).Items.Single().Total == 10,
    "handled filter aggregates only approved review members");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == main.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, main.Assignees.Take(199).ToList()));
Check((await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", AssignmentId = main.Id, Handled = null }, actor, default)).Items.Single().Total == 199,
    "revoked recipient excludes stale submitted member before assignment group counting");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == main.Id, Builders<WorkAssignment>.Update.Set(x => x.CurrentReviewerUserId, other));
Check(!(await inbox.AssignmentGroupsAsync(new() { Function = "REVIEW", AssignmentId = main.Id, Handled = null }, actor, default)).Items.Any(),
    "reviewer handover revokes every old group member and closed count");
Console.WriteLine($"PASS {count} checks; 200-member private fixture + ephemeral HTTP/JWT. No shared host or acceptance fixture changes.");

public class Proxy : DispatchProxy {
    public static T For<T>() where T : class => Create<T, Proxy>();
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        var type = method!.ReturnType; if (type == typeof(void)) return null; if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsInterface) return typeof(Proxy).GetMethod(nameof(For))!.MakeGenericMethod(type).Invoke(null, null);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) { var inner = type.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]); }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
