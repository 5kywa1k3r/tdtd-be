using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Works;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Hubs;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.Works;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
void Reject(Action action, AppErrorCode code) { try { action(); throw new Exception("Expected " + code); } catch (AppException ex) { Check(ex.Descriptor.Code == code, code.ToString()); } }
DateTime Day(int offset = 0) => DateTime.UtcNow.Date.AddDays(offset);
string Id() => ObjectId.GenerateNewId().ToString();
WorkUpdateRequest Request(Work w, DateTime? due, string? token = null) => new(w.Name, null, null, null, null, null, w.StartDate, w.EndDate, due, null, null, token);
WorkDatePolicy.Validate(Day(), null, null);
Check(true, "start-only accepted");
Reject(() => WorkDatePolicy.Validate(null, null, null), AppErrorCode.WORK_START_DATE_REQUIRED);
Reject(() => WorkDatePolicy.Validate(Day(), Day(-1), null), AppErrorCode.WORK_END_BEFORE_START);
Reject(() => WorkDatePolicy.Validate(Day(), null, Day(-1)), AppErrorCode.WORK_DUE_BEFORE_START);
Check(WorkDatePolicy.EffectiveDueDate(new Work { DueDate = Day(2), EndDate = Day(9) }) == Day(2), "deadline wins earlier end");
Check(WorkDatePolicy.EffectiveDueDate(new Work { DueDate = Day(9), EndDate = Day(2) }) == Day(9), "deadline wins later end");
Check(WorkAssignmentDatePolicy.ResolveWorkBoundaryEndDate(new Work { EndDate = Day(3) }) == Day(3), "assignment boundary falls back to end");
Check(WorkDatePolicy.EffectiveDueDate(new Work()) is null, "both dates null stay unknown");
var openWork = new Work { StartDate = Day(-10) };
var onceRequest = new SaveWorkAssignmentRequest { AssignmentType = "ONCE", AggregationType = "MATRIX",
    DynamicFormTemplateId = Id(), StartDate = Day(), AssigneeUserIds = new() { Id() } };
var normalizedOnce = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(
    WorkAssignmentScheduleHelper.NormalizeRequest(onceRequest), openWork, null, DateTime.UtcNow);
WorkAssignmentScheduleHelper.ValidateRequest(normalizedOnce, openWork);
Check(normalizedOnce.DueDate is null && normalizedOnce.DueAtUtc is null, "ONCE accepts no deadline without inventing dates");
var boundedWork = new Work { StartDate = Day(-10), DueDate = Day(5), EndDate = Day(9) };
var inheritedOnce = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(onceRequest, boundedWork, null, DateTime.UtcNow);
WorkAssignmentScheduleHelper.ValidateRequest(inheritedOnce, boundedWork);
Check(inheritedOnce.DueDate == Day(5) && inheritedOnce.DueAtUtc is null, "ONCE inherits Work deadline without separate report deadline input");
var parentOnce = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(onceRequest, boundedWork,
    new WorkAssignment { DueDate = Day(3) }, DateTime.UtcNow);
Check(parentOnce.DueDate == Day(3), "nearest parent deadline wins");
onceRequest.DueAtUtc = Day(2);
WorkAssignmentScheduleHelper.ValidateRequest(onceRequest, openWork);
Check(true, "explicit ONCE end still accepted");
onceRequest.DueAtUtc = Day(-1);
Reject(() => WorkAssignmentScheduleHelper.ValidateRequest(onceRequest, openWork), AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_BEFORE_ASSIGNMENT_START);
onceRequest.DueAtUtc = null;
onceRequest.AssignmentType = "PERIODIC_REPORT";
onceRequest.Schedule = new AssignmentScheduleDto("DAILY", Day(), null, null, null, null, null);
Reject(() => WorkAssignmentScheduleHelper.ValidateRequest(onceRequest, openWork), AppErrorCode.WORK_ASSIGNMENT_PERIODIC_WORK_DATE_RANGE_REQUIRED);
await AssignmentActivityControllerChecks.RunAsync(Check);
if (!args.Contains("--mongo")) { Console.WriteLine($"PASS {checks} pure checks"); return; }

// Always a new isolated database; never connect this harness to the acceptance DB.
var dbName = "work_dates_checks_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
var ctx = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = dbName }));
Console.WriteLine("FIXTURE_DATABASE " + dbName);
var actor = Id();
var tx = new DynamicFlowDefinitionTransactionRunner(ctx, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
var work = new Work { Id = Id(), AutoCode = "WORK-DATES", Name = "Kiểm ngày độc lập", StartDate = Day(-2), EndDate = Day(30), DueDate = Day(20), CreatedByUserId = actor, Status = WorkStatus.S1 };
await ctx.Works.InsertOneAsync(work);
work = await ctx.Works.Find(w => w.Id == work.Id).SingleAsync();
var child = new WorkAssignment { Id = Id(), WorkId = work.Id, Code = "WD-CHILD", Name = "Nhánh con", StartDate = Day(-2), DueDate = Day(15), AssignmentType = "PERIODIC_REPORT", IsActive = true, CreatedByUserId = actor,
    Schedule = new AssignmentSchedule { CycleType = "DAILY", StartDate = Day(-2) }, Assignees = new List<UserRef> { new() { UserId = actor } } };
var grandchild = new WorkAssignment { Id = Id(), WorkId = work.Id, Code = "WD-GRANDCHILD", Name = "Nhánh cháu", ParentAssignmentId = child.Id, StartDate = Day(-2), DueDate = Day(12), IsActive = true };
var done = new WorkAssignment { Id = Id(), WorkId = work.Id, Code = "WD-DONE", Name = "Đã hoàn thành", StartDate = Day(-2), DueDate = Day(12), CompletedAtUtc = Day(-1), IsActive = true };
await ctx.WorkAssignments.InsertManyAsync(new[] { child, grandchild, done });
var binding = new WorkTemplateAssignee { Id = Id(), WorkId = work.Id, WorkAssignmentId = child.Id!, AssigneeUserId = actor, AssignmentType = child.AssignmentType, DueDate = child.DueDate, StartDate = child.StartDate, Schedule = child.Schedule, IsActive = true };
await ctx.WorkTemplateAssignees.InsertOneAsync(binding);
var period = new WorkReportPeriod { Id = Id(), WorkId = work.Id, WorkAssignmentId = child.Id!, WorkTemplateAssigneeId = binding.Id, AssigneeUserId = actor, PeriodKey = Day(8).ToString("yyyyMMdd"), PeriodInstanceKey = Day(8).ToString("yyyyMMdd"), PeriodKind = WorkReportPeriodKind.Scheduled,
    PeriodStart = Day(8), PeriodEnd = Day(8), DueAtUtc = Day(8), IsActive = true, Status = WorkReportPeriodStatus.Approved };
await ctx.WorkReportPeriods.InsertOneAsync(period);
var beforePeriod = (await ctx.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync()).ToBsonDocument();
var request = Request(work, Day(5));
Reject(() => WorkDeadlineChange.Plan(work, request with { ExpectedUpdatedAtUtc = work.UpdatedAtUtc.AddSeconds(-1) }, actor, Array.Empty<WorkAssignment>()), AppErrorCode.WORK_DATES_CHANGED);
try { await WorkDeadlineChange.PrepareAsync(ctx, work, request, actor, default); throw new Exception("No confirmation"); }
catch (AppException ex) { Check(ex.Descriptor.Code == AppErrorCode.WORK_DATES_CONFIRM_REQUIRED, "contraction requires review"); }
var assignments = await ctx.WorkAssignments.Find(a => a.WorkId == work.Id).ToListAsync();
var plan = WorkDeadlineChange.Plan(work, request, actor, assignments);
var future = new WorkAssignment { Id = Id(), Code = "FUTURE", WorkId = work.Id, StartDate = Day(9), DueDate = Day(15) };
Reject(() => WorkDeadlineChange.Plan(work, request, actor, new[] { future }), AppErrorCode.WORK_ASSIGNMENT_COMPLETED_BEFORE_START);
var earlierRequest = request with { StartDate = Day(1) };
Reject(() => WorkDeadlineChange.Plan(work, earlierRequest, actor, assignments), AppErrorCode.WORK_ASSIGNMENT_START_OUT_OF_RANGE);
Check(plan.Affected.Count == 2 && plan.Affected.All(a => a.Id != done.Id), "both child levels clipped; completion history excluded");
request = request with { DeadlineConfirmationToken = plan.Token };
var prepared = await WorkDeadlineChange.PrepareAsync(ctx, work, request, actor, default);
work.UpdatedAtUtc = DateTime.UtcNow;
await tx.ExecuteAsync((session, ct) => WorkDeadlineChange.ApplyAsync(ctx, session, work, request, actor, prepared,
    Builders<Work>.Update.Set(w => w.DueDate, Day(5)).Set(w => w.UpdatedAtUtc, work.UpdatedAtUtc), ct));
Check((await ctx.WorkAssignments.Find(a => a.Id == child.Id).SingleAsync()).DueDate == Day(5), "child deadline persisted");
Check((await ctx.WorkAssignments.Find(a => a.Id == grandchild.Id).SingleAsync()).DueDate == Day(5), "grandchild deadline persisted");
Check((await ctx.WorkTemplateAssignees.Find(a => a.Id == binding.Id).SingleAsync()).DueDate == Day(5), "binding deadline consistent");
Check((await ctx.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync()).ToBsonDocument().Equals(beforePeriod), "approved period unchanged byte-for-byte BSON");
try { await tx.ExecuteAsync((session, ct) => WorkDeadlineChange.ApplyAsync(ctx, session, work, request, actor, prepared, Builders<Work>.Update.Set(w => w.DueDate, Day(2)), ct)); throw new Exception("Stale accepted"); }
catch (AppException ex) { Check(ex.Descriptor.Code == AppErrorCode.WORK_DATES_CHANGED, "stale confirmation rejected atomically"); }
Check((await ctx.Works.Find(w => w.Id == work.Id).SingleAsync()).DueDate == Day(5), "stale request did not change Work");

var fallback = new Work { Id = Id(), AutoCode = "END-ONLY", Name = "Hạn từ ngày kết thúc", EndDate = Day(-1), CreatedByUserId = actor, Status = WorkStatus.S1 };
var explicitFuture = new Work { Id = Id(), AutoCode = "DUE-WINS", Name = "Hạn còn ở tương lai", DueDate = Day(3), EndDate = Day(-1), CreatedByUserId = actor, Status = WorkStatus.S1 };
var open = new Work { Id = Id(), AutoCode = "OPEN", Name = "Chưa có hạn", CreatedByUserId = actor, Status = WorkStatus.S1 };
await ctx.Works.InsertManyAsync(new[] { fallback, explicitFuture, open });
foreach (var w in new[] { fallback, explicitFuture, open }) await ctx.WorkListDocRoles.InsertOneAsync(new WorkListDocRole { Id = Id(), WorkId = w.Id, UserId = actor, DocType = DocType.WORK, Name = w.Name, AutoCode = w.AutoCode, DueDate = null });
var rows = await WorkDeadlineReadQuery.WithDeadline(ctx.WorkListDocRoles.Aggregate().Match(r => r.UserId == actor), ctx.Works.CollectionNamespace.CollectionName).SortBy(r => r.DueDate).ToListAsync();
Check(rows.Single(r => r.WorkId == fallback.Id).DueDate == fallback.EndDate && rows.Single(r => r.WorkId == explicitFuture.Id).DueDate == explicitFuture.DueDate, "old list projections resolve authoritative deadline");
Check(rows.Select(r => r.WorkId).SequenceEqual(new[] { open.Id, fallback.Id, explicitFuture.Id }), "sorting uses fallback before pagination");
await ctx.Notifications.Indexes.CreateOneAsync(new CreateIndexModel<UserNotification>(Builders<UserNotification>.IndexKeys.Ascending(n => n.RecipientUserId).Ascending(n => n.EventKey), new CreateIndexOptions<UserNotification> { Unique = true, PartialFilterExpression = Builders<UserNotification>.Filter.Eq(n => n.IsDeleted, false) }));
var notifications = new NotificationService(ctx, Stub.For<IHubContext<NotificationsHub>>(), NullLogger<NotificationService>.Instance);
var dueJob = new NotificationDueScanJobService(ctx, notifications, Stub.For<IWorkStatusOperationLogService>(), new ConfigurationBuilder().Build(), NullLogger<NotificationDueScanJobService>.Instance);
await dueJob.ScanDueNotificationsAsync();
await dueJob.ScanDueNotificationsAsync();
var sent = await ctx.Notifications.Find(n => n.Type == UserNotificationTypes.WorkDue).ToListAsync();
Check(sent.Count == 1 && sent[0].WorkId == fallback.Id && sent[0].DueAtUtc == fallback.EndDate, "real notification persistence: fallback, explicit priority, unknown and repeat dedup");

// Extend this private fixture again, then run the real legacy materializer twice.
// Retained pending periods must survive just like approved periods.
child = await ctx.WorkAssignments.Find(a => a.Id == child.Id).SingleAsync();
var retainedPending = new WorkReportPeriod { Id = Id(), WorkId = work.Id, WorkAssignmentId = child.Id!, WorkTemplateAssigneeId = binding.Id, AssigneeUserId = actor,
    PeriodKey = Day(2).ToString("yyyyMMdd"), PeriodInstanceKey = Day(2).ToString("yyyyMMdd"), PeriodKind = WorkReportPeriodKind.Scheduled,
    PeriodStart = Day(2), PeriodEnd = Day(2), DueAtUtc = Day(2), IsActive = true, Status = WorkReportPeriodStatus.Pending,
    CreatedAtUtc = child.DeadlineRetainedPeriodsBeforeUtc!.Value.AddSeconds(-1) };
await ctx.WorkReportPeriods.InsertOneAsync(retainedPending);
var retainedBefore = (await ctx.WorkReportPeriods.Find(p => p.Id == retainedPending.Id).SingleAsync()).ToBsonDocument();
await ctx.WorkAssignments.UpdateOneAsync(a => a.Id == child.Id, Builders<WorkAssignment>.Update.Set(a => a.DueDate, Day(10)).Set(a => a.UpdatedAtUtc, DateTime.UtcNow));
await ctx.WorkTemplateAssignees.UpdateOneAsync(a => a.Id == binding.Id, Builders<WorkTemplateAssignee>.Update.Set(a => a.DueDate, Day(10)));
await ctx.Works.UpdateOneAsync(w => w.Id == work.Id, Builders<Work>.Update.Set(w => w.DueDate, Day(20)));
child = await ctx.WorkAssignments.Find(a => a.Id == child.Id).SingleAsync();
var materializer = new WorkAssignmentMaterializeJobService(ctx, tx, Stub.For<IWorkAssignmentStatusSyncService>(),
    Stub.For<IDocRoleReadModelProjectionService>(), Stub.For<IWorkStatusOperationLogService>(),
    NullLogger<WorkAssignmentMaterializeJobService>.Instance, new ConfigurationBuilder().Build());
await materializer.EnqueueOrTouchAsync(child, actor);
await materializer.ProcessPendingJobsAsync(maxJobs: 5, batchSize: 100);
var firstPeriods = await ctx.WorkReportPeriods.Find(p => p.WorkAssignmentId == child.Id).ToListAsync();
var job = await ctx.WorkAssignmentMaterializeJobs.Find(j => j.WorkAssignmentId == child.Id).SingleAsync();
Check(job.LastError is null && firstPeriods.Count > 2, "real materializer creates missing slots after extension");
await materializer.EnqueueOrTouchAsync(child, actor);
await materializer.ProcessPendingJobsAsync(maxJobs: 5, batchSize: 100);
var secondPeriods = await ctx.WorkReportPeriods.Find(p => p.WorkAssignmentId == child.Id).ToListAsync();
Check(secondPeriods.Count == firstPeriods.Count && secondPeriods.GroupBy(p => new { p.AssigneeUserId, p.PeriodKey }).All(g => g.Count() == 1), "repeated job reuses period identities without duplicates");
Check(secondPeriods.Single(p => p.Id == period.Id).ToBsonDocument().Equals(beforePeriod), "extension preserves approved period");
Check(secondPeriods.Single(p => p.Id == retainedPending.Id).ToBsonDocument().Equals(retainedBefore), "extension preserves retained empty pending period");
var onceOutside = new WorkAssignment { AssignmentType = "ONCE", DueDate = Day(2), DueAtUtc = Day(5), StartDate = Day() };
Check(WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(onceOutside, work, null, DateTime.UtcNow, 3).Count == 0, "once report outside contracted task deadline is not newly materialized");

var openOnce = new WorkAssignment { Id = Id(), WorkId = open.Id, Code = "OPEN-ONCE", Name = "Giao một lần chưa có hạn",
    AssignmentType = "ONCE", StartDate = Day(-5), CreatedAtUtc = DateTime.UtcNow, IsActive = true,
    CreatedByUserId = actor, Assignees = new() { new() { UserId = actor } } };
var openBinding = new WorkTemplateAssignee { Id = Id(), WorkId = open.Id, WorkAssignmentId = openOnce.Id,
    AssigneeUserId = actor, AssignmentType = "ONCE", StartDate = openOnce.StartDate, IsActive = true };
await ctx.WorkAssignments.InsertOneAsync(openOnce);
await ctx.WorkTemplateAssignees.InsertOneAsync(openBinding);
await materializer.EnqueueOrTouchAsync(openOnce, actor);
await materializer.ProcessPendingJobsAsync(maxJobs: 5, batchSize: 100);
var openPeriod = await ctx.WorkReportPeriods.Find(p => p.WorkAssignmentId == openOnce.Id).SingleAsync();
Check(openPeriod.DueAtUtc is null && openPeriod.PeriodEnd is null && !openPeriod.IsOverdue && !openPeriod.IsHistoricalData && openPeriod.Status == WorkReportPeriodStatus.Pending,
    "real ONCE job creates open pending period without fake deadline or historical classification");
var openQueue = await ctx.WorkAssignmentQueueItems.Find(q => q.WorkAssignmentId == openOnce.Id).SingleAsync();
Check(openQueue.DueAtUtc is null && openQueue.NextScanAtUtc > DateTime.UtcNow, "no-deadline queue stays nullable and avoids busy rescans");
Check(!WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(openOnce, openPeriod.PeriodStart, openPeriod.PeriodEnd,
    openPeriod.DueAtUtc ?? openPeriod.ReportDate, DateTime.UtcNow.AddDays(2)), "open ONCE does not become historical on later scans");
await materializer.EnqueueOrTouchAsync(openOnce, actor);
await materializer.ProcessPendingJobsAsync(maxJobs: 5, batchSize: 100);
Check(await ctx.WorkReportPeriods.CountDocumentsAsync(p => p.WorkAssignmentId == openOnce.Id) == 1, "open ONCE repeated job reuses one period");
await ctx.WorkAssignments.UpdateOneAsync(a => a.Id == openOnce.Id, Builders<WorkAssignment>.Update.Set(a => a.DueAtUtc, Day(4)));
openOnce = await ctx.WorkAssignments.Find(a => a.Id == openOnce.Id).SingleAsync();
await materializer.EnqueueOrTouchAsync(openOnce, actor);
await materializer.ProcessPendingJobsAsync(maxJobs: 5, batchSize: 100);
var datedPeriod = await ctx.WorkReportPeriods.Find(p => p.WorkAssignmentId == openOnce.Id).SingleAsync();
Check(datedPeriod.Id == openPeriod.Id && datedPeriod.PeriodKey == openPeriod.PeriodKey && datedPeriod.ReportDate == openPeriod.ReportDate && datedPeriod.DueAtUtc == Day(4),
    "adding ONCE deadline updates same occurrence and retains original key");
Check(await ctx.WorkAssignmentQueueItems.CountDocumentsAsync(q => q.WorkAssignmentId == openOnce.Id) == 1, "adding deadline reuses queue identity");
await ctx.WorkReportPeriods.UpdateOneAsync(p => p.Id == openPeriod.Id, Builders<WorkReportPeriod>.Update.Set(p => p.Status, WorkReportPeriodStatus.Approved));
var approvedOnce = (await ctx.WorkReportPeriods.Find(p => p.Id == openPeriod.Id).SingleAsync()).ToBsonDocument();
openOnce.DueAtUtc = Day(6);
await ctx.WorkAssignments.UpdateOneAsync(a => a.Id == openOnce.Id, Builders<WorkAssignment>.Update.Set(a => a.DueAtUtc, openOnce.DueAtUtc));
await materializer.EnqueueOrTouchAsync(openOnce, actor);
await materializer.ProcessPendingJobsAsync(maxJobs: 5, batchSize: 100);
Check((await ctx.WorkReportPeriods.Find(p => p.WorkAssignmentId == openOnce.Id).SingleAsync()).ToBsonDocument().Equals(approvedOnce), "ONCE deadline edit preserves approved occurrence without duplicate");
Console.WriteLine($"FIXTURE OpenOnce={openOnce.Id} OpenPeriod={openPeriod.Id}");

Console.WriteLine($"PASS {checks} checks; fixture retained; no acceptance DB mutation, startup or browser UAT claim.");
Console.WriteLine($"FIXTURE Work={work.Id} Child={child.Id} Grandchild={grandchild.Id} Period={period.Id}");

public class Stub : DispatchProxy
{
    public static T For<T>() where T : class => Create<T, Stub>();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsInterface) return typeof(Stub).GetMethod(nameof(For))!.MakeGenericMethod(type).Invoke(null, null);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GenericTypeArguments[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
