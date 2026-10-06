using System.Reflection;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Hubs;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.Works;

string Id() => ObjectId.GenerateNewId().ToString();
int checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var dbName = "work_inbox_checks_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var ctx = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = dbName }));
Console.WriteLine("FIXTURE_DATABASE " + dbName);
var actor = Id(); var other = Id(); var reviewer = Id(); var previousReviewer = Id();
var now = DateTime.UtcNow;
var work = new Work { Id = Id(), Name = "Inbox isolated fixture", AutoCode = "INBOX", Status = WorkStatus.S1, CreatedByUserId = actor };
await ctx.Works.InsertOneAsync(work);
WorkAssignment Assignment(string user, string name, bool form = true) => new() {
  Id = Id(), WorkId = work.Id, Code = name, Name = name, CreatedByUserId = previousReviewer,
  CurrentReviewerUserId = reviewer, Assignees = new() { new() { UserId = user, FullName = "Người thử" } },
  DynamicFormTemplateId = form ? Id() : null, IsActive = true, DueAtUtc = now.AddDays(-100), ProgressStatus = 0 };
WorkReportPeriod Period(WorkAssignment a, string user, int status = 0) => new() {
  Id = Id(), WorkId = work.Id, WorkAssignmentId = a.Id, WorkTemplateAssigneeId = Id(), AssigneeUserId = user,
  PeriodKey = "20261002", PeriodInstanceKey = Id(), IsActive = true, Status = (WorkReportPeriodStatus)status,
  ReportTitle = "Báo cáo thử", DueAtUtc = now.AddDays(-100), LastSubmittedAtUtc = now };
var mine = Assignment(actor, "Mine"); var theirs = Assignment(other, "Theirs"); var direct = Assignment(actor, "Direct", false);
mine.CreatedByUserId = actor;
mine.Assignees.Add(new UserRef { UserId = other, FullName = "Người xin sao chép" });
var completed = Assignment(actor, "Completed", false); completed.CompletedDate = now; completed.ProgressStatus = 2;
var flow = Assignment(actor, "Flow"); flow.FlowInstanceId = Id();
await ctx.WorkAssignments.InsertManyAsync(new[] { mine, theirs, direct, completed, flow });
// A pending copy request is actionable only while the pinned source still exists.
await ctx.DynamicFormTemplates.InsertOneAsync(new DynamicFormTemplate { Id = mine.DynamicFormTemplateId!, Code = "INBOX-FIXTURE", Name = "Fixture source", CreatedByUserId = actor });
var pending = Period(mine, actor); var submitted = Period(theirs, other, 6); var mineSubmitted = Period(mine, actor, 2); var hiddenFlow = Period(flow, actor);
pending.ReportTitle = "";
var submittedReport = new WorkAssignmentReport { Id = Id(), WorkId = work.Id, WorkAssignmentId = theirs.Id, WorkReportPeriodId = submitted.Id,
  AssigneeUserId = other, PeriodKey = submitted.PeriodKey, PeriodInstanceKey = submitted.PeriodInstanceKey,
  IsActive = true, IsCurrent = true, Status = WorkAssignmentReportStatus.Submitted, SubmittedAtUtc = now, LifecycleRevision = 2 };
submitted.CurrentReportId = submittedReport.Id;
await ctx.WorkAssignmentReports.InsertOneAsync(submittedReport);
await ctx.WorkReportPeriods.InsertManyAsync(new[] { pending, submitted, mineSubmitted, hiddenFlow });
await ctx.WorkAssignmentReportLogs.InsertOneAsync(new WorkAssignmentReportLog { Id = Id(), WorkId = work.Id, WorkAssignmentId = theirs.Id,
  WorkReportPeriodId = submitted.Id, WorkAssignmentReportId = submittedReport.Id, Action = "REVIEW_RETURN", ActionAtUtc = now.AddHours(-1) });
var clone = new DynamicFormCloneRequest { Id = Id(), WorkId = work.Id, WorkAssignmentId = mine.Id, AssignmentOwnerUserId = actor,
  RequesterUserId = other, DynamicFormTemplateId = mine.DynamicFormTemplateId!, Status = "PENDING" };
await ctx.DynamicFormCloneRequests.InsertOneAsync(clone);
var notifications = new NotificationService(ctx, Stub.For<IHubContext<NotificationsHub>>(), NullLogger<NotificationService>.Instance);
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkInbox:DueSoonHours"] = "24", ["WorkInbox:NotifyReviewRequired"] = "true" }).Build();
var inbox = new WorkInboxService(ctx, notifications, Stub.For<IWorkPermissionService>(), cfg);
var defaults = new WorkInboxService(ctx, notifications, Stub.For<IWorkPermissionService>(), new ConfigurationBuilder().Build());
Check((await inbox.ItemAsync("report:" + pending.Id, actor, default))!.Title == mine.Name, "empty legacy period title falls back to assignment name");
await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == pending.Id, Builders<WorkReportPeriod>.Update.Set(x => x.ReportTitle, "   "));
Check((await inbox.ItemAsync("report:" + pending.Id, actor, default))!.Title == mine.Name, "whitespace period title falls back to assignment name");
Check(defaults.DueSoonHours == 24 && !defaults.NotifyReviewRequired, "approved default is 24 hours; bell has four basic event groups");
var first = await inbox.SearchAsync(new(), actor, default);
Check(first.Items.Count == 3 && first.Items.Any(x => x.Id == "report:" + pending.Id), "own pending + direct + request; no submitted/completed/Flow leakage");
Check(first.Items.Single(x => x.Function == "REPORT").State == "OVERDUE", "100-day-old obligation survives notification lookback");
Check((await inbox.SearchAsync(new(), other, default)).Items.All(x => x.WorkId == work.Id && x.AssignmentId == theirs.Id), "actor cannot enumerate another actor's obligations");
var review = await inbox.SearchAsync(new(), reviewer, default);
Check(review.Items.Count == 1 && review.Items[0].IsResubmission && review.Items[0].AssigneeName == "Người thử", "current reviewer sees overdue submission and log-derived resubmission");
Check((await inbox.SearchAsync(new(), previousReviewer, default)).Items.Count == 0, "old issuer loses review after handover even without read-model projection");
Check((await inbox.ItemAsync("assignment:" + theirs.Id, reviewer, default)) is { RequiresAction: false }, "historical assignment notification opens current reviewer context without writing obligation");
Check(await inbox.ItemAsync("assignment:" + theirs.Id, previousReviewer, default) is null, "historical notification context does not retain old reviewer after handover");
Check(await inbox.ItemAsync("review:" + submitted.Id, actor, default) is null, "foreign deep-link is rejected");
var page1 = await inbox.SearchAsync(new() { PageSize = 1 }, actor, default);
var page2 = await inbox.SearchAsync(new() { PageSize = 1, CursorId = page1.NextCursorId, CursorPriority = page1.NextCursorPriority, CursorDueAtUtc = page1.NextCursorDueAtUtc }, actor, default);
Check(page1.HasMore && page1.Items[0].Id != page2.Items[0].Id, "seek cursor progresses without duplicate item");
Check((await inbox.SummaryAsync(actor, default)).Total == 3, "summary counts source obligations, independent of unread count");
var notification = (await notifications.CreateManyAsync(new[] { new NotificationCommand { RecipientUserId = actor, Type = "REPORT_DUE", Title = "Due", EventKey = "old-due", WorkId = work.Id, WorkAssignmentId = mine.Id, WorkReportPeriodId = pending.Id, OccurredAtUtc = now.AddDays(-100) } }))[0];
await notifications.MarkManyReadAsync(new[] { notification.Id }, actor);
Check((await inbox.SearchAsync(new(), actor, default)).Items.Count == 3, "reading does not resolve actionable work");
await notifications.MarkManyReadAsync(new[] { notification.Id }, other);
Check((await inbox.HistoryAsync(new(), other, default)).Items.Count == 0, "history and read operations stay actor-scoped");
await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == pending.Id, Builders<WorkReportPeriod>.Update.Set(x => x.Status, WorkReportPeriodStatus.Approved));
Check(!(await inbox.ItemAsync("report:" + pending.Id, actor, default))!.RequiresAction, "old due notification resolves to handled context");
Check((await inbox.SearchAsync(new(), actor, default)).Items.Count == 2, "approval removes obligation without deleting notification");
Check((await inbox.HistoryAsync(new(), actor, default)).Items.Count == 1, "history retained after completion");
await ctx.DynamicFormCloneRequests.UpdateOneAsync(x => x.Id == clone.Id, Builders<DynamicFormCloneRequest>.Update.Set(x => x.Status, "APPROVED"));
Check((await inbox.ItemAsync("clone:" + clone.Id, other, default)) is { RequiresAction: false }, "requester can read approved clone context without acquiring approver action");
Check((await inbox.ItemAsync("assignment:" + completed.Id, actor, default)) is { RequiresAction: false }, "completion requires a decision or completed status together with its day");
await notifications.CreateManyAsync(Enumerable.Range(0, 25).Select(i => new NotificationCommand { RecipientUserId = actor, Type = "ASSIGNMENT_ASSIGNED", Title = "Assigned", WorkId = work.Id, WorkAssignmentId = direct.Id, EventKey = "assigned:" + i, OccurredAtUtc = now.AddMinutes(i) }));
var recent = await inbox.RecentAsync(actor, default);
Check(recent.Items.Count == 20 && recent.RecentUnreadCount == 20 && !recent.Items.Any(x => x.Notification.Id == notification.Id), "bell only latest 20 and matching unread badge");
var history = await inbox.HistoryAsync(new() { PageSize = 20 }, actor, default);
var older = await inbox.HistoryAsync(new() { PageSize = 20, CursorId = history.NextCursorId, CursorOccurredAtUtc = history.NextCursorOccurredAtUtc }, actor, default);
Check(history.HasMore && older.Items.Count == 6 && older.Items.Any(x => x.Notification.Id == notification.Id), "history reaches notification that fell out of bell");
var dueSoon = Period(mine, actor); dueSoon.DueAtUtc = now.AddHours(12); await ctx.WorkReportPeriods.InsertOneAsync(dueSoon);
var job = new WorkInboxNotificationJob(ctx, notifications, inbox, NullLogger<WorkInboxNotificationJob>.Instance);
await job.ScanDueNotificationsAsync();
Check(await ctx.Notifications.CountDocumentsAsync(x => x.Type == "REPORT_DUE_SOON" && x.WorkReportPeriodId == dueSoon.Id) == 1, "configured due-soon producer emits report event");
Check(await ctx.Notifications.CountDocumentsAsync(x => x.Type == "REPORT_REVIEW_REQUIRED" && x.RecipientUserId == reviewer) == 1, "review-required producer uses current reviewer");
var before = await ctx.Notifications.CountDocumentsAsync(FilterDefinition<UserNotification>.Empty);
await job.ScanDueNotificationsAsync();
Check(await ctx.Notifications.CountDocumentsAsync(FilterDefinition<UserNotification>.Empty) == before, "scan retry deduplicates notification events");
await ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == submittedReport.Id, Builders<WorkAssignmentReport>.Update.Set(x => x.Status, WorkAssignmentReportStatus.Approved).Set(x => x.ApprovedAtUtc, now).Set(x => x.LifecycleRevision, 3));
Check((await inbox.SearchAsync(new(), reviewer, default)).Items.Count == 0, "authoritative approved report wins over lagging submitted period");
await job.ScanDueNotificationsAsync();
Check(await ctx.Notifications.CountDocumentsAsync(x => x.Type == "REPORT_APPROVED" && x.RecipientUserId == other) == 1, "approval event routes to report owner");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == mine.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, new List<UserRef> { new() { UserId = other } }));
Check(await inbox.ItemAsync("report:" + dueSoon.Id, actor, default) is null, "handover revokes old assignee deep-link despite stale period actor");
var controller = new WorkInboxController(inbox, notifications) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actor) }, "test")) } } };
Check(await controller.Items(new() { CursorId = "partial" }, default) is BadRequestObjectResult, "partial cursor rejected");
Check(await controller.Read(new() { Ids = new() { "invalid" } }, default) is BadRequestResult, "malformed read id rejected");
Check(new NotificationsController().Retired() is ObjectResult { StatusCode: 410 }, "legacy API explicitly retired");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(ctx); builder.Services.AddSingleton<INotificationService>(notifications);
builder.Services.AddSingleton(inbox); builder.Services.AddControllers().AddApplicationPart(typeof(WorkInboxController).Assembly);
var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
  options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "inbox-fixture", ValidateAudience = true,
    ValidAudience = "inbox-fixture", ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey, ClockSkew = TimeSpan.Zero };
});
builder.Services.AddAuthorization();
await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync();
try {
  using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
  Check((await client.GetAsync("/api/me/work-inbox/summary")).StatusCode == HttpStatusCode.Unauthorized, "HTTP rejects missing JWT");
  var jwt = new JwtSecurityToken("inbox-fixture", "inbox-fixture", new[] { new Claim(ClaimTypes.NameIdentifier, actor) },
    expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
  client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
  Check((await client.GetAsync("/api/me/work-inbox/summary")).IsSuccessStatusCode, "HTTP accepts fixture JWT at new summary API");
  Check((int)(await client.PostAsJsonAsync("/api/notifications/search", new { pageSize = 20 })).StatusCode == 410, "legacy API returns HTTP 410 after authenticated migration");
  var response = await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { pageSize = 30, actorUserId = other });
  var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  Check(response.IsSuccessStatusCode && json.RootElement.GetProperty("items").EnumerateArray().All(x => x.GetProperty("assignmentId").GetString() == direct.Id),
    "HTTP camelCase payload uses JWT actor, ignores client-supplied actor");
  Check((await client.GetAsync("/api/me/work-inbox/items/review%3A" + submitted.Id)).StatusCode == HttpStatusCode.NotFound, "HTTP foreign obligation returns 404");
  Check((await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { cursorId = "partial" })).StatusCode == HttpStatusCode.BadRequest, "HTTP rejects partial seek cursor");
  Check((await client.GetAsync("/api/me/work-inbox/notifications/recent")).IsSuccessStatusCode, "HTTP recent notification endpoint resolves");
  Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/history/search", new { pageSize = 30 })).IsSuccessStatusCode, "HTTP notification history endpoint resolves");
  Check((await client.PostAsJsonAsync("/api/me/work-inbox/groups/search", new { pageSize = 30 })).IsSuccessStatusCode, "HTTP grouped source endpoint resolves");
  Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/read", new { ids = new[] { notification.Id } })).StatusCode == HttpStatusCode.NoContent, "HTTP read endpoint resolves");
} finally { await app.StopAsync(); }
var selfAssignment = Assignment(actor, "ExistingSelfReviewer"); selfAssignment.CurrentReviewerUserId = actor;
await ctx.WorkAssignments.InsertOneAsync(selfAssignment);
var selfPeriod = Period(selfAssignment, actor, 2);
var selfReport = new WorkAssignmentReport { Id = Id(), WorkId = work.Id, WorkAssignmentId = selfAssignment.Id,
  WorkReportPeriodId = selfPeriod.Id, AssigneeUserId = actor, PeriodKey = selfPeriod.PeriodKey, PeriodInstanceKey = selfPeriod.PeriodInstanceKey,
  IsActive = true, IsCurrent = true, Status = WorkAssignmentReportStatus.Submitted, SubmittedAtUtc = now };
selfPeriod.CurrentReportId = selfReport.Id;
await ctx.WorkAssignmentReports.InsertOneAsync(selfReport); await ctx.WorkReportPeriods.InsertOneAsync(selfPeriod);
Check((await inbox.SearchAsync(new() { Function = "REVIEW" }, actor, default)).Items.Any(x => x.Id == "review:" + selfPeriod.Id),
  "existing current-reviewer authority is preserved when reviewer is also assignee");
Check(!(await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Any(x => x.Target.PeriodId == selfPeriod.Id),
  "submitted self-review period is not a writing obligation");
Console.WriteLine($"PASS {checks} checks; isolated Mongo + HTTP/JWT fixture; no acceptance data or product browser UAT claim.");

// Actual Mongo aggregation, including exact reopened period under completed Work/ancestors.
var handledReview = await inbox.ItemAsync("review:" + submitted.Id, reviewer, default);
Check(handledReview is { RequiresAction: false, Target.ReadOnly: false }, "handled review retains lifecycle entry; transaction authority remains separate");
var ancestor = Assignment(actor, "Completed ancestor", false); ancestor.CompletedAtUtc = now;
await ctx.WorkAssignments.InsertOneAsync(ancestor);
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == mine.Id, Builders<WorkAssignment>.Update
    .Set(x => x.Assignees, mine.Assignees).Set(x => x.ParentAssignmentId, ancestor.Id)
    .Set(x => x.CompletionReopenedAtUtc, now).Set(x => x.CompletionReviewPeriodId, dueSoon.Id));
await ctx.Works.UpdateOneAsync(x => x.Id == work.Id, Builders<Work>.Update.Set(x => x.Status, WorkStatus.S3));
var reopenedQueue = await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default);
Check(reopenedQueue.Items.Count == 1 && reopenedQueue.Items[0].Target.PeriodId == dueSoon.Id, "only reopened period returns to queue below completed Work and ancestor");
await ctx.Works.UpdateOneAsync(x => x.Id == work.Id, Builders<Work>.Update.Set(x => x.CompletedAtUtc, now.AddSeconds(1)));
Check(!(await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Any(), "new Work closure invalidates stale reopen in real Mongo Inbox");
await ctx.Works.UpdateOneAsync(x => x.Id == work.Id, Builders<Work>.Update.Set(x => x.CompletedAtUtc, now.AddSeconds(-1)));
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == ancestor.Id, Builders<WorkAssignment>.Update.Set(x => x.CompletedAtUtc, now.AddSeconds(1)));
Check(!(await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Any(), "new ancestor closure invalidates stale reopen in real Mongo Inbox");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == ancestor.Id, Builders<WorkAssignment>.Update.Set(x => x.CompletedAtUtc, now));
using (var approvalSession = await ctx.Db.Client.StartSessionAsync())
{
    approvalSession.StartTransaction();
    await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.RecordFirstApprovalAsync(
        ctx, approvalSession, mine.Id, now.AddSeconds(2), default, dueSoon.Id, WorkAssignmentReportStatus.Approved);
    await approvalSession.CommitTransactionAsync();
}
Check((await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Count == 1, "reactivation does not settle reopen in Mongo");
using (var approvalSession = await ctx.Db.Client.StartSessionAsync())
{
    approvalSession.StartTransaction();
    await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.RecordFirstApprovalAsync(
        ctx, approvalSession, mine.Id, now.AddSeconds(2), default, dueSoon.Id, WorkAssignmentReportStatus.Submitted);
    await approvalSession.CommitTransactionAsync();
}
var settledAssignment = await ctx.WorkAssignments.Find(x => x.Id == mine.Id).SingleAsync();
Check(settledAssignment.CompletionReviewPeriodId == null && settledAssignment.CompletionProjectionPending && settledAssignment.CompletionRevision == 1,
    "fresh approval atomically expires reopen and marks projection pending in Mongo");
Check(!(await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Any(), "settled correction no longer bypasses completed Work in Inbox");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == mine.Id, Builders<WorkAssignment>.Update.Set(x => x.CompletionReviewPeriodId, dueSoon.Id));
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == ancestor.Id, Builders<WorkAssignment>.Update.Set(x => x.IsActive, false));
Check(!(await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Any(), "inactive ancestor blocks reopened queue");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == ancestor.Id, Builders<WorkAssignment>.Update.Set(x => x.IsActive, true).Set(x => x.ParentAssignmentId, mine.Id));
Check(!(await inbox.SearchAsync(new() { Function = "REPORT" }, actor, default)).Items.Any(), "cyclic ancestor chain fails closed in Mongo pipeline");
Console.WriteLine($"PASS {checks} total checks including reopened queue and handled review actions; own fixture only.");

public class Stub : DispatchProxy {
  public static T For<T>() where T : class => Create<T, Stub>();
  protected override object? Invoke(MethodInfo? method, object?[]? args) {
    var type = method!.ReturnType;
    if (type == typeof(void)) return null;
    if (type == typeof(Task)) return Task.CompletedTask;
    if (type.IsInterface) return typeof(Stub).GetMethod(nameof(For))!.MakeGenericMethod(type).Invoke(null, null);
    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
      var inner = type.GenericTypeArguments[0];
      return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
    }
    return type.IsValueType ? Activator.CreateInstance(type) : null;
  }
}
