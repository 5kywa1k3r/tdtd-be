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
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Hubs;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.Works;

var dbName = "inbox_filter_checks_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var ctx = new MongoDbContext(Options.Create(new MongoOptions {
    ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = dbName }));
Console.WriteLine("FIXTURE_DATABASE " + dbName);
string Id() => ObjectId.GenerateNewId().ToString();
int count = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
var actor = Id(); var other = Id(); var formOwner = Id();
var now = DateTime.UtcNow; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
var work = new Work { Id = Id(), Name = "Nhiệm vụ lọc riêng", CreatedByUserId = other };
await ctx.Works.InsertOneAsync(work);
WorkAssignment Assignment(string name, DateTime? due, bool completed = false) => new() {
    Id = Id(), WorkId = work.Id, Code = "FIXTURE", Name = name, IsActive = true, CreatedByUserId = other,
    CurrentReviewerUserId = other, Assignees = [new() { UserId = actor, FullName = "Tài khoản thử" }],
    DueAtUtc = due, ProgressStatus = completed ? 2 : 0 };
var overdue = Assignment("Hồ sơ [x].* quá hạn", now.AddDays(-1));
var near = Assignment("Hồ sơ sắp hạn", now.AddHours(12));
var open = Assignment("Hồ sơ chưa tới hạn", now.AddDays(3));
var undated = Assignment("Hồ sơ không hạn", null);
var completed = Assignment("Hồ sơ hoàn tất", now.AddDays(3), true);
var foreign = Assignment("Hồ sơ riêng người khác", now.AddDays(-1)); foreign.Assignees = [new() { UserId = other }];
var reviewAssignment = Assignment("Phần việc cần duyệt", now.AddDays(-2));
reviewAssignment.CurrentReviewerUserId = actor; reviewAssignment.Assignees = [new() { UserId = other }];
reviewAssignment.DynamicFormTemplateId = Id();
var submittedAssignment = Assignment("Phần việc đã nộp", now.AddDays(2)); submittedAssignment.DynamicFormTemplateId = Id();
var draftAssignment = Assignment("Phần việc chưa nộp", now.AddDays(2)); draftAssignment.DynamicFormTemplateId = Id();
var cloneAssignment = Assignment("Phần việc xin sao chép", null); cloneAssignment.DynamicFormTemplateId = Id();
await ctx.WorkAssignments.InsertManyAsync(new[] { overdue, near, open, undated, completed, foreign, reviewAssignment,
    submittedAssignment, draftAssignment, cloneAssignment });
WorkReportPeriod Period(WorkAssignment assignment, string user, WorkReportPeriodStatus status) => new() {
    Id = Id(), WorkId = work.Id, WorkAssignmentId = assignment.Id, WorkTemplateAssigneeId = Id(), AssigneeUserId = user,
    IsActive = true, Status = status, PeriodKey = "20261006", PeriodInstanceKey = Id(), DueAtUtc = assignment.DueAtUtc,
    ReportTitle = "Báo cáo " + assignment.Name };
var review = Period(reviewAssignment, other, WorkReportPeriodStatus.OverdueSubmitted);
var submitted = Period(submittedAssignment, actor, WorkReportPeriodStatus.Submitted);
var draft = Period(draftAssignment, actor, WorkReportPeriodStatus.Draft);
WorkAssignmentReport Report(WorkReportPeriod period, WorkAssignmentReportStatus status) => new() {
    Id = Id(), WorkId = period.WorkId, WorkAssignmentId = period.WorkAssignmentId, WorkReportPeriodId = period.Id,
    AssigneeUserId = period.AssigneeUserId, IsActive = true, IsCurrent = true, Status = status,
    PeriodKey = period.PeriodKey, PeriodInstanceKey = period.PeriodInstanceKey };
var reviewReport = Report(review, WorkAssignmentReportStatus.Submitted); review.CurrentReportId = reviewReport.Id;
var submittedReport = Report(submitted, WorkAssignmentReportStatus.Submitted); submitted.CurrentReportId = submittedReport.Id;
await ctx.WorkReportPeriods.InsertManyAsync(new[] { review, submitted, draft });
await ctx.WorkAssignmentReports.InsertManyAsync(new[] { reviewReport, submittedReport });
await ctx.DynamicFormTemplates.InsertOneAsync(new DynamicFormTemplate { Id = cloneAssignment.DynamicFormTemplateId!,
    Code = "FIXTURE-CLONE", Name = "Nguồn riêng", CreatedByUserId = formOwner });
var pendingClone = new DynamicFormCloneRequest { Id = Id(), WorkId = work.Id, WorkAssignmentId = cloneAssignment.Id,
    DynamicFormTemplateId = cloneAssignment.DynamicFormTemplateId!, AssignmentOwnerUserId = other, RequesterUserId = actor,
    Status = "PENDING", Requester = new() { UserId = actor } };
await ctx.DynamicFormCloneRequests.InsertOneAsync(pendingClone);
var notifications = new NotificationService(ctx, Proxy.For<IHubContext<NotificationsHub>>(), NullLogger<NotificationService>.Instance);
var inbox = new WorkInboxService(ctx, notifications, Proxy.For<IWorkPermissionService>(), new ConfigurationBuilder().Build());
var pending = await inbox.SearchAsync(new(), actor, default);
Check(pending.Items.Count == 6 && pending.Items.All(x => x.ProcessingState == "PENDING"), "default includes six actual pending obligations only");
var handled = await inbox.SearchAsync(new() { Handled = true }, actor, default);
Check(handled.Items.Count == 2 && handled.Items.All(x => x.ProcessingState == "HANDLED"), "handled includes completed assignment and own submitted report");
Check(handled.Items.Any(x => x.Id == "report:" + submitted.Id) && !handled.Items.Any(x => x.Id == "clone:" + pendingClone.Id), "submitted writing is handled but requester awaiting approval is informational");
var both = await inbox.SearchAsync(new() { Handled = null }, actor, default);
Check(both.Items.Count == 8 && both.Items.All(x => x.AssignmentId != foreign.Id), "explicit null combines real obligations without foreign or informational context");
Check((await inbox.SummaryAsync(actor, default, handled: true)).Total == 2 && (await inbox.SummaryAsync(actor, default, handled: null)).Total == 8, "summary uses same handled scope");
Check((await inbox.SearchAsync(new() { SearchText = "[x].*" }, actor, default)).Items.Single().AssignmentId == overdue.Id, "search escapes regex metacharacters and matches literal name");
Check((await inbox.SearchAsync(new() { SearchText = "HỒ SƠ SẮP" }, actor, default)).Items.Single().AssignmentId == near.Id, "case insensitive search matches assignment name");
Check((await inbox.SearchAsync(new() { SearchText = work.Name }, actor, default)).Items.Count == 6, "search matches parent work name");
var urgency = await inbox.SearchAsync(new() { State = "OVERDUE" }, actor, default);
Check(urgency.Items.Count == 2 && urgency.Items.Any(x => x.Function == "REVIEW" && x.State == "WAITING_REVIEW"), "overdue urgency includes current waiting review obligation");
Check((await inbox.SearchAsync(new() { State = "DUE_SOON" }, actor, default)).Items.Single().AssignmentId == near.Id, "near deadline filters priority one");
Check((await inbox.SearchAsync(new() { State = "OPEN" }, actor, default)).Items.Count == 3, "no deadline warning includes far and undated pending obligations");
Check((await inbox.SearchAsync(new() { State = "OVERDUE", Handled = true }, actor, default)).Items.Count == 0, "deadline urgency never marks handled records overdue");
var bounded = new WorkInboxSearchRequest { Function = "ASSIGNMENT", DueFromUtc = near.DueAtUtc, DueBeforeUtc = open.DueAtUtc };
Check((await inbox.SearchAsync(bounded, actor, default)).Items.Single().AssignmentId == near.Id, "deadline range includes start excludes end and excludes missing deadline");
Check((await inbox.SummaryAsync(actor, default, searchText: "[x].*", state: "OVERDUE")).Total == 1, "summary applies search and urgency before group counts");
var groupJson = JsonSerializer.SerializeToElement(await inbox.GroupsAsync(new() { SearchText = "[x].*" }, actor, default));
Check(groupJson.GetProperty("Items")[0].GetProperty("Total").GetInt64() == 1, "group counts filter before aggregation");
var pagedIds = new List<string>(); var cursor = new WorkInboxSearchRequest { Handled = null, SearchText = "Hồ sơ", PageSize = 1 };
do { var page = await inbox.SearchAsync(cursor, actor, default); pagedIds.AddRange(page.Items.Select(x => x.Id));
    if (!page.HasMore) break; cursor.CursorId = page.NextCursorId; cursor.CursorPriority = page.NextCursorPriority; cursor.CursorDueAtUtc = page.NextCursorDueAtUtc;
} while (true);
Check(pagedIds.Count == 5 && pagedIds.Distinct().Count() == 5, "seek pagination after filters has no duplicate or skipped matches");
await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == review.Id, Builders<WorkReportPeriod>.Update.Set(x => x.Status, WorkReportPeriodStatus.Approved));
await ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == reviewReport.Id, Builders<WorkAssignmentReport>.Update.Set(x => x.Status, WorkAssignmentReportStatus.Approved));
Check((await inbox.SearchAsync(new() { Handled = true, Function = "REVIEW" }, actor, default)).Items.Single().Id == "review:" + review.Id, "approved review joins handled from actual report state");
Check(!(await inbox.SearchAsync(new(), actor, default)).Items.Any(x => x.Id == "review:" + review.Id), "approved review leaves pending queue");
await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == review.Id, Builders<WorkReportPeriod>.Update.Set(x => x.Status, WorkReportPeriodStatus.OverdueSubmitted));
await ctx.WorkAssignmentReports.UpdateOneAsync(x => x.Id == reviewReport.Id, Builders<WorkAssignmentReport>.Update.Set(x => x.Status, WorkAssignmentReportStatus.Submitted));
foreach (var mismatch in new[] { "assignment", "period", "assignee" }) {
    var wrongReport = Report(draft, WorkAssignmentReportStatus.Submitted);
    if (mismatch == "assignment") wrongReport.WorkAssignmentId = reviewAssignment.Id;
    if (mismatch == "period") wrongReport.WorkReportPeriodId = review.Id;
    if (mismatch == "assignee") wrongReport.AssigneeUserId = other;
    await ctx.WorkAssignmentReports.InsertOneAsync(wrongReport);
    await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == draft.Id, Builders<WorkReportPeriod>.Update.Set(x => x.CurrentReportId, wrongReport.Id));
    var unavailable = await inbox.ItemAsync("report:" + draft.Id, actor, default);
    Check(unavailable is { ProcessingState: "UNAVAILABLE", RequiresAction: false } && unavailable.Target.ReadOnly && unavailable.Target.ReportId is null,
        "mismatched current report " + mismatch + " fails closed without foreign target report id");
    Check(!(await inbox.SearchAsync(new() { Handled = null }, actor, default)).Items.Any(x => x.Id == "report:" + draft.Id)
        && (await inbox.SummaryAsync(actor, default, handled: true)).Total == 2,
        "mismatched " + mismatch + " report never leaks into pending handled or counts");
    await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == draft.Id, Builders<WorkReportPeriod>.Update.Set(x => x.CurrentReportId, null));
}
var wrongDraft = Report(review, WorkAssignmentReportStatus.Draft);
await ctx.WorkAssignmentReports.InsertOneAsync(wrongDraft);
await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == draft.Id, Builders<WorkReportPeriod>.Update.Set(x => x.CurrentReportId, wrongDraft.Id));
Check((await inbox.SearchAsync(new(), actor, default)).Items.Count == 5
    && (await inbox.ItemAsync("report:" + draft.Id, actor, default)) is { ProcessingState: "UNAVAILABLE", RequiresAction: false },
    "foreign draft pointer cannot become actionable writing obligation");
await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == draft.Id, Builders<WorkReportPeriod>.Update.Set(x => x.CurrentReportId, null));
await ctx.Works.UpdateOneAsync(x => x.Id == work.Id, Builders<Work>.Update.Set(x => x.Status, WorkStatus.S3));
Check((await inbox.AssignmentDetailAsync(open.Id, actor, default))?.State == "PARENT_COMPLETED",
    "popup distinguishes parent completed from recipient assignment completion");
Check((await inbox.AssignmentDetailAsync(completed.Id, actor, default))?.State == "COMPLETED",
    "actual assignment completion takes precedence over parent completed");
Check((await inbox.SummaryAsync(actor, default)).Total == 0 && !(await inbox.SearchAsync(new(), actor, default)).Items.Any()
    && (await inbox.ItemAsync("assignment:" + open.Id, actor, default)) is { RequiresAction: false },
    "completed parent clears pending counts and action without claiming report submission");
await ctx.Works.UpdateOneAsync(x => x.Id == work.Id, Builders<Work>.Update.Set(x => x.Status, WorkStatus.S1));

UserNotification Notification(DateTime time, bool read, string? assignment, string type = "ASSIGNMENT_ASSIGNED", string? period = null) => new() {
    Id = Id(), RecipientUserId = actor, WorkId = work.Id, WorkAssignmentId = assignment, WorkReportPeriodId = period,
    Type = type, Severity = "INFO", Title = "Fixture", EventKey = Id(), OccurredAtUtc = time, ReadAtUtc = read ? now : null };
var nOld = Notification(now.AddDays(-2), false, overdue.Id);
var nStart = Notification(now.AddDays(-1), true, completed.Id);
var nTie1 = Notification(now, false, submittedAssignment.Id, "REPORT_APPROVED", submitted.Id);
var nTie2 = Notification(now, true, foreign.Id);
var nEnd = Notification(now.AddDays(1), false, null);
await ctx.Notifications.InsertManyAsync(new[] { nOld, nStart, nTie1, nTie2, nEnd });
Check((await inbox.HistoryAsync(new() { UnreadOnly = true }, actor, default)).Items.Count == 3, "unread filter selects null readAt only");
Check((await inbox.HistoryAsync(new() { UnreadOnly = false }, actor, default)).Items.Count == 2, "read filter selects populated readAt only");
Check((await inbox.HistoryAsync(new() { UnreadOnly = null }, actor, default)).Items.Count == 5, "null read filter returns both");
var rangedHistory = await inbox.HistoryAsync(new() { OccurredFromUtc = nStart.OccurredAtUtc, OccurredBeforeUtc = nEnd.OccurredAtUtc }, actor, default);
Check(rangedHistory.Items.Count == 3 && rangedHistory.Items.All(x => x.Notification.Id != nEnd.Id), "history dates include start exclude end");
Check((await inbox.HistoryAsync(new() { Types = ["REPORT_APPROVED"], OccurredFromUtc = now }, actor, default)).Items.Single().Notification.Id == nTie1.Id, "history type and date filters combine");
var historyIds = new List<string>(); var nc = new NotificationSearchRequest { PageSize = 1, OccurredFromUtc = nStart.OccurredAtUtc, OccurredBeforeUtc = nEnd.OccurredAtUtc };
do { var page = await inbox.HistoryAsync(nc, actor, default); historyIds.AddRange(page.Items.Select(x => x.Notification.Id));
    if (!page.HasMore) break; nc.CursorId = page.NextCursorId; nc.CursorOccurredAtUtc = page.NextCursorOccurredAtUtc;
} while (true);
Check(historyIds.Count == 3 && historyIds.Distinct().Count() == 3 && historyIds[0] == nTie2.Id, "history filtered seek uses stable descending id at equal time");
var currentHistory = (await inbox.HistoryAsync(new(), actor, default)).Items.ToDictionary(x => x.Notification.Id, x => x.Notification);
Check(currentHistory[nStart.Id].ProcessingState == "HANDLED" && currentHistory[nOld.Id].ProcessingState == "PENDING", "history processing comes from current source independently of read status");
Check(currentHistory[nOld.Id].ProcessingPriority == 0 && currentHistory[nStart.Id].ProcessingPriority is null, "current source urgency exists only for pending notification links");
Check(currentHistory[nTie1.Id].ProcessingState == "HANDLED" && currentHistory[nTie2.Id].ProcessingState == "UNAVAILABLE", "own submitted report handled and foreign historical source unavailable");
Check(currentHistory[nOld.Id].AssignmentName == overdue.Name && currentHistory[nOld.Id].WorkName == work.Name, "legacy notification name enrichment remains bounded and correct");
await notifications.MarkReadAsync(nOld.Id, actor, false);
Check((await inbox.SearchAsync(new(), actor, default)).Items.Count == 6, "reading notification does not resolve source obligations");
await ctx.WorkAssignments.UpdateOneAsync(x => x.Id == completed.Id, Builders<WorkAssignment>.Update.Set(x => x.Assignees, new List<UserRef> { new() { UserId = other } }));
Check(!(await inbox.SearchAsync(new() { Handled = null }, actor, default)).Items.Any(x => x.AssignmentId == completed.Id), "revocation removes handled source from combined queue");
Check((await inbox.HistoryAsync(new(), actor, default)).Items.Single(x => x.Notification.Id == nStart.Id).Notification.ProcessingState == "UNAVAILABLE", "revoked recipient historical source becomes unavailable rather than handled");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(ctx); builder.Services.AddSingleton<INotificationService>(notifications); builder.Services.AddSingleton(inbox);
builder.Services.AddControllers().AddApplicationPart(typeof(WorkInboxController).Assembly);
var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new() {
    ValidateIssuer = true, ValidIssuer = "filter-fixture", ValidateAudience = true, ValidAudience = "filter-fixture",
    ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey, ClockSkew = TimeSpan.Zero });
builder.Services.AddAuthorization(); await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync();
try {
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    Check((await client.GetAsync("/api/me/work-inbox/summary")).StatusCode == HttpStatusCode.Unauthorized, "HTTP scope rejects missing JWT");
    var token = new JwtSecurityToken("filter-fixture", "filter-fixture", [new Claim(ClaimTypes.NameIdentifier, actor)],
        expires: DateTime.UtcNow.AddMinutes(2), signingCredentials: new(signingKey, SecurityAlgorithms.HmacSha256));
    client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    var combined = await client.GetFromJsonAsync<WorkInboxSummary>("/api/me/work-inbox/summary?handled=all");
    Check(combined?.Total == 7, "HTTP summary handled all selects explicit null combined scope");
    Check((await client.GetFromJsonAsync<WorkInboxSummary>("/api/me/work-inbox/summary?handled="))?.Total == 7, "HTTP summary empty handled is compatible with combined scope");
    Check((await client.GetFromJsonAsync<WorkInboxSummary>("/api/me/work-inbox/summary?handled=true"))?.Total == 1, "HTTP summary handled true filters completed source");
    Check((await client.GetFromJsonAsync<WorkInboxSummary>("/api/me/work-inbox/summary?state=OVERDUE&searchText=%5Bx%5D.*"))?.Total == 1, "HTTP summary validates and applies literal search urgency filters");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/items/search", new { handled = (bool?)null, actorUserId = other, searchText = "Hồ sơ" })).IsSuccessStatusCode, "HTTP JSON explicit null scope ignores forged actor");
    foreach (var payload in new object[] { new { state = "DONE" }, new { searchText = new string('x', 201) },
        new { dueFromUtc = now, dueBeforeUtc = now }, new { dueFromUtc = now.AddDays(1), dueBeforeUtc = now } })
        Check((await client.PostAsJsonAsync("/api/me/work-inbox/items/search", payload)).StatusCode == HttpStatusCode.BadRequest, "HTTP rejects invalid queue state length or date range");
    Check((await client.PostAsJsonAsync("/api/me/work-inbox/groups/search", new { state = "BAD" })).StatusCode == HttpStatusCode.BadRequest, "HTTP groups reject invalid scope");
    Check((await client.GetAsync("/api/me/work-inbox/summary?state=BAD")).StatusCode == HttpStatusCode.BadRequest, "HTTP summary rejects invalid scope");
    Check((await client.GetAsync("/api/me/work-inbox/summary?handled=BAD")).StatusCode == HttpStatusCode.BadRequest, "HTTP summary rejects invalid handled selector");
    foreach (var payload in new object[] { new { occurredFromUtc = now, occurredBeforeUtc = now }, new { pageSize = 51 }, new { cursorOccurredAtUtc = now } })
        Check((await client.PostAsJsonAsync("/api/me/work-inbox/notifications/history/search", payload)).StatusCode == HttpStatusCode.BadRequest, "HTTP history rejects invalid range size or cursor");
    var history = await client.PostAsJsonAsync("/api/me/work-inbox/notifications/history/search", new { unreadOnly = false, occurredFromUtc = now.AddDays(-1), occurredBeforeUtc = now.AddDays(1) });
    Check(history.IsSuccessStatusCode && (await history.Content.ReadFromJsonAsync<WorkInboxNotificationPage>())?.Items.Count == 2, "HTTP read and date filters combine on actual actor history");
    await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == draft.Id, Builders<WorkReportPeriod>.Update.Set(x => x.CurrentReportId, reviewReport.Id));
    var invalidItem = await client.GetFromJsonAsync<WorkInboxItem>("/api/me/work-inbox/items/report%3A" + draft.Id);
    Check(invalidItem is { ProcessingState: "UNAVAILABLE", RequiresAction: false } && invalidItem.Target.ReportId is null && invalidItem.Target.ReadOnly,
        "HTTP JWT foreign current report pointer returns unavailable read-only context without target leakage");
    Check((await client.GetFromJsonAsync<WorkInboxSummary>("/api/me/work-inbox/summary?handled=true"))?.Total == 1,
        "HTTP foreign submitted pointer does not increase handled count");
    await ctx.WorkReportPeriods.UpdateOneAsync(x => x.Id == draft.Id, Builders<WorkReportPeriod>.Update.Set(x => x.CurrentReportId, null));
} finally { await app.StopAsync(); }
Console.WriteLine($"PASS {count} checks; isolated Mongo + ephemeral HTTP/JWT; no shared host or acceptance fixture changes.");

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
