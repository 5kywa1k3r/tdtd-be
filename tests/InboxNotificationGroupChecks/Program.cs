using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
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
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Hubs;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.Works;

var dbName = "inbox_notification_group_checks_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var ctx = new MongoDbContext(Options.Create(new MongoOptions {
    ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = dbName }));
Console.WriteLine("FIXTURE_DATABASE " + dbName);
string Id() => ObjectId.GenerateNewId().ToString();
int count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
var actor = Id(); var other = Id();
var now = DateTime.UtcNow; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
var work = new Work { Id = Id(), Name = "Nhiệm vụ notification grouping", CreatedByUserId = actor };
await ctx.Works.InsertOneAsync(work);
var a = new WorkAssignment { Id = Id(), WorkId = work.Id, Code = "FIXTURE", Name = "Đầu việc 200 đơn vị",
    CreatedByUserId = actor, CurrentReviewerUserId = actor, DynamicFormTemplateId = Id(), IsActive = true };
var periods = new List<WorkReportPeriod>(); var reports = new List<WorkAssignmentReport>(); var events = new List<UserNotification>();
for (var i = 0; i < 200; i++) {
    var user = Id(); a.Assignees.Add(new() { UserId = user, FullName = "Fixture đơn vị " + i });
    var p = new WorkReportPeriod { Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkTemplateAssigneeId = Id(), AssigneeUserId = user,
        PeriodKey = "20261006", PeriodInstanceKey = Id(), IsActive = true, Status = i == 199 ? WorkReportPeriodStatus.Approved : WorkReportPeriodStatus.OverdueSubmitted,
        DueAtUtc = now.AddDays(-1), ReportTitle = "Báo cáo đơn vị " + i };
    var r = new WorkAssignmentReport { Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkReportPeriodId = p.Id, AssigneeUserId = user,
        PeriodKey = p.PeriodKey, PeriodInstanceKey = p.PeriodInstanceKey, IsActive = true, IsCurrent = true,
        Status = i == 199 ? WorkAssignmentReportStatus.Approved : WorkAssignmentReportStatus.Submitted };
    p.CurrentReportId = r.Id; periods.Add(p); reports.Add(r);
    events.Add(new() { Id = Id(), RecipientUserId = actor, WorkId = work.Id, WorkAssignmentId = a.Id, WorkReportPeriodId = p.Id,
        Type = "REPORT_REVIEW_REQUIRED", Severity = "INFO", Title = "Fixture cần duyệt", EventKey = Id(), RequiresAction = true,
        ActionState = "OPEN", OccurredAtUtc = now.AddSeconds(i - 200), ReadAtUtc = i < 40 ? now : null });
}
await ctx.WorkAssignments.InsertOneAsync(a); await ctx.WorkReportPeriods.InsertManyAsync(periods);
await ctx.WorkAssignmentReports.InsertManyAsync(reports); await ctx.Notifications.InsertManyAsync(events);
UserNotification Event(string type, DateTime time, string recipient, string? assignment = null) => new() {
    Id = Id(), RecipientUserId = recipient, WorkId = work.Id, WorkAssignmentId = assignment, Type = type, Severity = "INFO", Title = "Fixture " + type,
    EventKey = Id(), OccurredAtUtc = time, RequiresAction = true, ActionState = "OPEN" };
var due = Event("REPORT_DUE", now.AddSeconds(-1), actor, a.Id);
var clone1 = Event("DYNAMIC_FORM_CLONE_REQUESTED", now.AddSeconds(-1), actor, a.Id); clone1.RequestId = Id();
var clone2 = Event("DYNAMIC_FORM_CLONE_REQUESTED", now.AddSeconds(-1), actor, a.Id); clone2.RequestId = Id();
var assigned = Event("ASSIGNMENT_ASSIGNED", now.AddSeconds(-1), actor, a.Id);
var foreign = Event("REPORT_REVIEW_REQUIRED", now, other, a.Id);
await ctx.Notifications.InsertManyAsync(new[] { due, clone1, clone2, assigned, foreign });
var approvedAssignment = new WorkAssignment { Id = Id(), WorkId = work.Id, Code = "APPROVED-FIXTURE", Name = "Đầu việc báo cáo đã duyệt",
    CreatedByUserId = other, CurrentReviewerUserId = other, DynamicFormTemplateId = Id(), IsActive = true, Assignees = [new() { UserId = actor }] };
await ctx.WorkAssignments.InsertOneAsync(approvedAssignment);
for (var i = 0; i < 2; i++) {
    var approvedPeriod = new WorkReportPeriod { Id = Id(), WorkId = work.Id, WorkAssignmentId = approvedAssignment.Id, WorkTemplateAssigneeId = Id(),
        AssigneeUserId = actor, PeriodKey = "APPROVED" + i, PeriodInstanceKey = Id(), IsActive = true, Status = WorkReportPeriodStatus.Approved };
    var approvedReport = new WorkAssignmentReport { Id = Id(), WorkId = work.Id, WorkAssignmentId = approvedAssignment.Id,
        WorkReportPeriodId = approvedPeriod.Id, AssigneeUserId = actor, PeriodKey = approvedPeriod.PeriodKey, PeriodInstanceKey = approvedPeriod.PeriodInstanceKey,
        IsActive = true, IsCurrent = true, Status = WorkAssignmentReportStatus.Approved };
    approvedPeriod.CurrentReportId = approvedReport.Id;
    await ctx.WorkReportPeriods.InsertOneAsync(approvedPeriod); await ctx.WorkAssignmentReports.InsertOneAsync(approvedReport);
    var approvedEvent = Event("REPORT_APPROVED", now.AddSeconds(-2), actor, approvedAssignment.Id); approvedEvent.WorkReportPeriodId = approvedPeriod.Id;
    await ctx.Notifications.InsertOneAsync(approvedEvent);
}
var notifications = new NotificationService(ctx, Proxy.For<IHubContext<NotificationsHub>>(), NullLogger<NotificationService>.Instance);
var inbox = new WorkInboxService(ctx, notifications, Proxy.For<IWorkPermissionService>(), new ConfigurationBuilder().Build());
var page = await inbox.NotificationGroupsAsync(new() { PageSize = 20 }, actor, default);
var group = page.Items.Single(x => x.Notification.Type == "REPORT_REVIEW_REQUIRED");
var knownCapture = new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc).AddTicks(12345);
var completedMillisecond = WorkInboxNotificationGroups.CompletedCreationWatermark(knownCapture);
Check(completedMillisecond == new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc)
    && completedMillisecond.Ticks % TimeSpan.TicksPerMillisecond == 0,
    "creation watermark floors Mongo precision and excludes current incomplete capture millisecond");
Check(page.Items.Count == 6 && !page.HasMore, "206 actor notifications collapse200reviewevents and2approvedevents into six rows");
Check(group.Total == 200 && group.UnreadCount == 160 && page.RecentUnreadCount == 166, "notification totals and unread sum cover full members before page limit");
var approvedGroup = page.Items.Single(x => x.Notification.Type == "REPORT_APPROVED");
Check(approvedGroup.Total == 2 && approvedGroup.Notification.ProcessingState == "HANDLED" && approvedGroup.ItemId == "assignment:" + approvedAssignment.Id,
    "approved events group for owner REPORT scope and derive actual handled writing obligations");
Check(group.Notification.Id == events[^1].Id && page.Items.All(x => x.AsOfUtc == group.AsOfUtc)
    && group.AsOfUtc > events[^1].OccurredAtUtc, "latest event uses business time while every page group shares true creation snapshot watermark");
Check(group.Notification.ProcessingState == "PENDING" && group.Notification.ProcessingPriority == 0,
    "latest approved member cannot mask199actualpendingreview obligations");
Check(group.ItemId == "assignment:" + a.Id && group.Notification.WorkAssignmentId == a.Id && group.Notification.WorkId == work.Id,
    "report group routing identifies authoritative assignment scope rather than arbitrary latest member");
Check(group.Notification.AssignmentName == a.Name && group.Notification.WorkName == work.Name, "group representative keeps bounded legacy subject enrichment");
Check(page.Items.Count(x => x.Notification.Type == "DYNAMIC_FORM_CLONE_REQUESTED") == 2
    && page.Items.Where(x => x.Notification.Type == "DYNAMIC_FORM_CLONE_REQUESTED").Select(x => x.GroupKey).Distinct().Count() == 2,
    "independent clone actions remain separate notification id groups");
Check(!page.Items.Any(x => x.Notification.Id == foreign.Id), "foreign recipient cannot contribute latest event group or unread totals");
var unread = (await inbox.NotificationGroupsAsync(new() { UnreadOnly = true, Types = ["REPORT_REVIEW_REQUIRED"] }, actor, default)).Items.Single();
Check(unread.Total == 160 && unread.UnreadCount == 160, "unread filter applies before notification grouping");
var read = (await inbox.NotificationGroupsAsync(new() { UnreadOnly = false, Types = ["REPORT_REVIEW_REQUIRED"] }, actor, default)).Items.Single();
Check(read.Total == 40 && read.UnreadCount == 0 && read.Notification.ProcessingState == "PENDING", "read filter is independent of actual unresolved assignment processing");
var range = (await inbox.NotificationGroupsAsync(new() { Types = ["REPORT_REVIEW_REQUIRED"], OccurredFromUtc = now.AddSeconds(-100), OccurredBeforeUtc = now.AddSeconds(-50) }, actor, default)).Items.Single();
Check(range.Total == 50 && range.Notification.OccurredAtUtc == now.AddSeconds(-51), "notification business date bounds filter members independently of creation watermark");
var cursorProbe = await inbox.NotificationGroupsAsync(new() { PageSize = 1 }, actor, default);
Check(cursorProbe.NextCursorOccurredAtUtc == cursorProbe.Items[0].Notification.OccurredAtUtc
    && cursorProbe.NextCursorOccurredAtUtc != cursorProbe.Items[0].AsOfUtc, "pagination cursor remains business event time rather than creation snapshot cutoff");
var cursorIds = new List<string>(); var cursor = new NotificationSearchRequest { PageSize = 1 };
do { var next = await inbox.NotificationGroupsAsync(cursor, actor, default); cursorIds.AddRange(next.Items.Select(x => x.GroupKey));
    if (!next.HasMore) break; cursor.CursorId = next.NextCursorId; cursor.CursorOccurredAtUtc = next.NextCursorOccurredAtUtc;
} while (true);
Check(cursorIds.Count == 6 && cursorIds.Distinct().Count() == 6 && cursorIds.SequenceEqual(page.Items.Select(x => x.GroupKey)),
    "group cursor handles equal latest times with stable composite key without duplicate or skipped group");
var jumped = await inbox.NotificationGroupsAsync(new() { PageSize = 1, PageNumber = 4 }, actor, default);
Check(jumped.Items.Single().GroupKey == page.Items[3].GroupKey, "group page number jumps directly to sorted fourth group");
var raw = await inbox.HistoryAsync(new() { PageSize = 10, PageNumber = 3 }, actor, default);
Check(raw.Items.Count == 10 && raw.Items.All(x => x.Notification.Id != foreign.Id), "legacy raw history preserves actor scope and supports direct page jump");
var members = await inbox.SearchAsync(new() { Function = "REVIEW", AssignmentId = a.Id, PageSize = 50, PageNumber = 4 }, actor, default);
Check(members.Items.Count == 49 && !members.HasMore, "existing member list directly jumps to fourth page of199pending rows");
Check((await inbox.SummaryAsync(actor, default, assignmentId: a.Id)).ByFunction["REVIEW"] == 199
    && (await inbox.SummaryAsync(actor, default, assignmentId: Id())).Total == 0, "summary assignment prefill scopes match real grouped destinations");
var beforeObligations = (await inbox.SummaryAsync(actor, default)).Total;
var latestEventId = events[^1].Id;
var eventIds = events.Select(x => x.Id).ToList();
var newer = Event("REPORT_REVIEW_REQUIRED", now.AddSeconds(1), actor, a.Id); newer.WorkReportPeriodId = periods[0].Id;
var sameBusiness = Event("REPORT_REVIEW_REQUIRED", events[^1].OccurredAtUtc, actor, a.Id); sameBusiness.WorkReportPeriodId = periods[0].Id;
var backdatedBusiness = Event("REPORT_REVIEW_REQUIRED", events[0].OccurredAtUtc.AddDays(-1), actor, a.Id); backdatedBusiness.WorkReportPeriodId = periods[0].Id;
var captureMillisecond = Event("REPORT_REVIEW_REQUIRED", events[0].OccurredAtUtc, actor, a.Id); captureMillisecond.WorkReportPeriodId = periods[0].Id;
captureMillisecond.CreatedAtUtc = group.AsOfUtc.AddMilliseconds(1);
await ctx.Notifications.InsertManyAsync(new[] { newer, sameBusiness, backdatedBusiness, captureMillisecond });
await inbox.MarkNotificationGroupsReadAsync([new() { GroupKey = group.GroupKey, AsOfUtc = group.AsOfUtc }], actor, default);
Check(await ctx.Notifications.CountDocumentsAsync(x => eventIds.Contains(x.Id) && x.ReadAtUtc != null) == 200,
    "snapshot group read marks all200members through bounded50id batches");
Check((await ctx.Notifications.Find(x => x.Id == newer.Id).SingleAsync()).ReadAtUtc is null
    && (await ctx.Notifications.Find(x => x.Id == foreign.Id).SingleAsync()).ReadAtUtc is null, "group read preserves newer unseen and foreign recipient notifications");
var lateIds = new[] { sameBusiness.Id, backdatedBusiness.Id, captureMillisecond.Id };
Check(await ctx.Notifications.CountDocumentsAsync(x => lateIds.Contains(x.Id) && x.ReadAtUtc == null) == 3,
    "same or backdated business events with post snapshot or capture millisecond creation remain unread");
Check((await ctx.Notifications.Find(x => x.Id == latestEventId).SingleAsync()).ActionState == "OPEN"
    && (await inbox.SummaryAsync(actor, default)).Total == beforeObligations, "reading group changes no action state or source obligation");
var afterRead = (await inbox.NotificationGroupsAsync(new() { Types = ["REPORT_REVIEW_REQUIRED"] }, actor, default)).Items.Single();
Check(afterRead.Total == 204 && afterRead.UnreadCount == 4 && afterRead.Notification.ProcessingState == "PENDING", "all four unseen arrivals remain unread while group processing stays pending");
await inbox.MarkNotificationGroupsReadAsync([new() { GroupKey = "notification:" + clone1.Id, AsOfUtc = group.AsOfUtc }], actor, default);
Check((await ctx.Notifications.Find(x => x.Id == clone1.Id).SingleAsync()).ReadAtUtc is not null
    && (await ctx.Notifications.Find(x => x.Id == clone2.Id).SingleAsync()).ReadAtUtc is null, "individual non-report descriptor reads only selected independent clone event");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(ctx); builder.Services.AddSingleton<INotificationService>(notifications); builder.Services.AddSingleton(inbox);
builder.Services.AddControllers().AddApplicationPart(typeof(WorkInboxController).Assembly);
var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidIssuer = "notification-group-fixture", ValidateAudience = true, ValidAudience = "notification-group-fixture",
    ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey, ClockSkew = TimeSpan.Zero });
builder.Services.AddAuthorization(); await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
try {
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    var path = "/api/me/work-inbox/notifications/groups/search";
    Check((await client.PostAsJsonAsync(path, new { pageSize = 20 })).StatusCode == HttpStatusCode.Unauthorized, "HTTP notification groups require JWT actor");
    var token = new JwtSecurityToken("notification-group-fixture", "notification-group-fixture", [new Claim(ClaimTypes.NameIdentifier, actor)],
        expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: new(signingKey, SecurityAlgorithms.HmacSha256));
    client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    var httpPage = await client.PostAsJsonAsync(path, new { pageSize = 1, pageNumber = 2, actorUserId = other });
    Check(httpPage.IsSuccessStatusCode && (await httpPage.Content.ReadFromJsonAsync<WorkInboxNotificationGroupPage>())!.Items.Count == 1,
        "HTTP direct notification group page jump ignores forged actor");
    foreach (var payload in new object[] { new { pageNumber = 0 }, new { pageNumber = int.MaxValue, pageSize = 50 },
        new { pageNumber = 2, cursorId = group.GroupKey, cursorOccurredAtUtc = group.AsOfUtc }, new { cursorId = events[^1].Id, cursorOccurredAtUtc = group.AsOfUtc } })
        Check((await client.PostAsJsonAsync(path, payload)).StatusCode == HttpStatusCode.BadRequest, "HTTP groups reject invalid jump mixed cursor overflow or bare id cursor");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { pageNumber = 0 })).StatusCode == HttpStatusCode.BadRequest,
        "HTTP raw member jump rejects zero page");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/assignment-groups/search", new { function = "REVIEW", pageNumber = 2, cursorId = a.Id,
        cursorPriority = 0, cursorDueAtUtc = now })).StatusCode == HttpStatusCode.BadRequest, "HTTP assignment group rejects jump combined with seek cursor");
    Check((await client.GetAsync("/api/me/work-inbox/notifications/groups/recent")).IsSuccessStatusCode, "HTTP grouped bell recent endpoint available");
    Check((await client.GetFromJsonAsync<WorkInboxSummary>("/api/me/work-inbox/summary?assignmentId=" + a.Id))?.Total == 199,
        "HTTP summary honors authoritative assignment id navigation prefill");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/groups/read", new { groups = new[] { new { groupKey = "notification:" + foreign.Id, asOfUtc = now } } })).StatusCode == HttpStatusCode.NoContent
        && (await ctx.Notifications.Find(x => x.Id == foreign.Id).SingleAsync()).ReadAtUtc is null, "HTTP group read forged foreign descriptor cannot modify other actor notification");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/groups/read", new { groups = Enumerable.Range(0, 21).Select(_ => new { groupKey = group.GroupKey, asOfUtc = group.AsOfUtc }) })).StatusCode == HttpStatusCode.BadRequest,
        "HTTP snapshot group read enforces20descriptor bound");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/groups/read", new { groups = new[] { new { groupKey = "assignment:" + a.Id + ":UNSUPPORTED", asOfUtc = now } } })).StatusCode == HttpStatusCode.BadRequest,
        "HTTP group read validates supported event key formats");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/groups/read", new { groups = new[] { new { groupKey = group.GroupKey, asOfUtc = DateTime.UtcNow.AddHours(1) } } })).StatusCode == HttpStatusCode.BadRequest,
        "HTTP group read rejects future creation snapshot cutoff");
} finally { await app.StopAsync(); }
var legacy1 = Event("REPORT_DUE", now.AddSeconds(2), actor);
var legacy2 = Event("REPORT_DUE", now.AddSeconds(2), actor);
await ctx.Notifications.InsertManyAsync(new[] { legacy1, legacy2 });
await ctx.Notifications.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(legacy2.Id)),
    new BsonDocument("$set", new BsonDocument("workAssignmentId", "")));
var legacyGroup = (await inbox.NotificationGroupsAsync(new() { Types = ["REPORT_DUE"] }, actor, default)).Items.Single(x => x.GroupKey.StartsWith("work:", StringComparison.Ordinal));
Check(legacyGroup.Total == 2 && legacyGroup.GroupKey == "work:" + work.Id + ":REPORT_DUE", "legacy missing and empty assignment ids group by work without invalid composite cursor");
await inbox.MarkNotificationGroupsReadAsync([new() { GroupKey = legacyGroup.GroupKey, AsOfUtc = legacyGroup.AsOfUtc }], actor, default);
Check((await ctx.Notifications.Find(x => x.Id == legacy1.Id).SingleAsync()).ReadAtUtc is not null
    && (await ctx.Notifications.Find(x => x.Id == legacy2.Id).SingleAsync()).ReadAtUtc is not null
    && (await ctx.Notifications.Find(x => x.Id == due.Id).SingleAsync()).ReadAtUtc is null,
    "legacy work group snapshot read handles empty assignment and preserves explicit assignment group events");
var missingWork = Event("REPORT_APPROVED", now.AddSeconds(3), actor, approvedAssignment.Id); missingWork.WorkId = null;
await ctx.Notifications.InsertOneAsync(missingWork);
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == approvedAssignment.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, new List<UserRef> { new() { UserId = other } }));
var unavailableApproved = (await inbox.NotificationGroupsAsync(new() { Types = ["REPORT_APPROVED"] }, actor, default)).Items.Single();
Check(unavailableApproved.Notification.WorkId == work.Id && unavailableApproved.Notification.ProcessingState == "UNAVAILABLE",
    "missing notification work id fills authoritative assignment work id even after obligation access is revoked");
var malformed = Event("REPORT_DUE", now.AddSeconds(4), actor);
await ctx.Notifications.InsertOneAsync(malformed);
await ctx.Notifications.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(malformed.Id)),
    new BsonDocument("$set", new BsonDocument { { "workAssignmentId", "legacy-invalid" }, { "workId", "" } }));
var malformedGroup = (await inbox.NotificationGroupsAsync(new() { Types = ["REPORT_DUE"] }, actor, default)).Items.Single(x => x.Notification.Id == malformed.Id);
Check(malformedGroup.GroupKey == "notification:" + malformed.Id && WorkInboxNotificationGroups.ValidGroupKey(malformedGroup.GroupKey),
    "malformed legacy identifiers fall back to independent valid notification key");
await inbox.MarkNotificationGroupsReadAsync([new() { GroupKey = malformedGroup.GroupKey, AsOfUtc = malformedGroup.AsOfUtc }], actor, default);
Check((await ctx.Notifications.Find(x => x.Id == malformed.Id).SingleAsync()).ReadAtUtc is not null, "raw fallback malformed legacy event remains safely actor readable");
var futureBusiness = Event("REPORT_DUE", DateTime.UtcNow.AddHours(1), actor, a.Id);
futureBusiness.CreatedAtUtc = group.AsOfUtc.AddMilliseconds(-1);
await ctx.Notifications.InsertOneAsync(futureBusiness);
var futureGroup = (await inbox.NotificationGroupsAsync(new() { Types = ["REPORT_DUE"], WorkAssignmentId = a.Id }, actor, default)).Items.Single();
Check(futureGroup.Notification.Id == futureBusiness.Id && futureGroup.Notification.OccurredAtUtc > futureGroup.AsOfUtc,
    "pre snapshot creation with future business timestamp is visible in latest sorted group");
await inbox.MarkNotificationGroupsReadAsync([new() { GroupKey = futureGroup.GroupKey, AsOfUtc = futureGroup.AsOfUtc }], actor, default);
Check((await ctx.Notifications.Find(x => x.Id == futureBusiness.Id).SingleAsync()).ReadAtUtc is not null,
    "visible pre snapshot creation is read even when business event time lies after cutoff");
var secondWork = new Work { Id = Id(), Name = "Nhiệm vụ thứ hai", CreatedByUserId = actor };
await ctx.Works.InsertOneAsync(secondWork);
await ctx.WorkAssignments.InsertOneAsync(new WorkAssignment { Id = Id(), WorkId = secondWork.Id, Code = "GROUP-PAGE", Name = "Phần việc fixture",
    IsActive = true, CreatedByUserId = other, CurrentReviewerUserId = other, Assignees = [new() { UserId = actor }] });
var workPage1 = System.Text.Json.JsonSerializer.SerializeToElement(await inbox.GroupsAsync(new() { PageSize = 1, PageNumber = 1 }, actor, default));
var workPage2 = System.Text.Json.JsonSerializer.SerializeToElement(await inbox.GroupsAsync(new() { PageSize = 1, PageNumber = 2 }, actor, default));
Check(workPage1.GetProperty("Items")[0].GetProperty("WorkId").GetString() != workPage2.GetProperty("Items")[0].GetProperty("WorkId").GetString(),
    "legacy work groups honor page number skip after grouping and sort");
Console.WriteLine($"PASS {count} checks; 200-notification/200-unit private fixture + ephemeral HTTP/JWT. No shared host or acceptance fixture changes.");

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
