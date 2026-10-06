using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using System.Text.Json;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkInbox;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

// Pure product checks. No DB, API, seed, clock override or notification dispatch.
var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var at = new DateTime(2026, 10, 6, 3, 46, 0, DateTimeKind.Utc);
var work = new Work { Id = "111111111111111111111111", Name = "Nhiệm vụ tập huấn" };
var assignment = new WorkAssignment { Id = "222222222222222222222222", WorkId = work.Id,
    Name = "Mẫu 1", CurrentReviewerUserId = "333333333333333333333333" };
var report = new WorkAssignmentReport { Id = "444444444444444444444444", WorkId = work.Id,
    WorkAssignmentId = assignment.Id, WorkReportPeriodId = "555555555555555555555555", AssigneeUserId = "666666666666666666666666",
    Status = WorkAssignmentReportStatus.Approved, SubmittedAtUtc = at, ApprovedAtUtc = at.AddMinutes(3) };
WorkReportLifecycleProjectionOutboxEntry Event(int revision, string operation, string from, string to, DateTime when, string? reason = null)
    => WorkReportLifecycleOutboxContract.CreateEntry(new WorkReportLifecycleOutboxSeed(
        "command-" + revision, revision, operation, assignment.CurrentReviewerUserId, from, to, true, true,
        revision, "hash-" + revision, when, BusinessReason: reason));
report.LifecycleProjectionOutbox = [Event(1, "SUBMIT", "DRAFT", "SUBMITTED", at),
    Event(2, "REVIEW_RETURN", "SUBMITTED", "DRAFT", at.AddMinutes(1), "Bổ sung kết quả"),
    Event(3, "SUBMIT", "DRAFT", "SUBMITTED", at.AddMinutes(2)),
    Event(4, "REVIEW_APPROVE", "SUBMITTED", "APPROVED", at.AddMinutes(3))];
var commands = WorkReportNotificationEvents.Build(report, assignment, work, true);
Check(commands.Count == 4, "all four committed events survive a scan after approval");
Check(commands.Select(x => x.Type).SequenceEqual(new[] { "REPORT_REVIEW_REQUIRED", "REPORT_RETURNED", "REPORT_RESUBMITTED", "REPORT_APPROVED" }), "event order and resubmission label");
Check(commands[0].RecipientUserId == assignment.CurrentReviewerUserId && commands[2].RecipientUserId == assignment.CurrentReviewerUserId, "both submissions notify reviewer");
Check(commands[1].RecipientUserId == report.AssigneeUserId && commands[3].RecipientUserId == report.AssigneeUserId, "return and approval notify recipient");
Check(commands[1].Body == "Bổ sung kết quả", "return reason retained");
Check(commands.All(x => x.AssignmentName == "Mẫu 1" && x.WorkReportPeriodId == report.WorkReportPeriodId), "names and exact period scope");
Check(commands.Select(x => x.EventKey).SequenceEqual(WorkReportNotificationEvents.Build(report, assignment, work, true).Select(x => x.EventKey)), "retry uses stable event keys");
Check(commands.Select(x => x.EventKey).Distinct().Count() == 4, "one key per transition");
Check(WorkReportNotificationEvents.Build(report, assignment, work, false).Select(x => x.Type).SequenceEqual(new[] { "REPORT_RETURNED", "REPORT_APPROVED" }), "configuration suppresses review events only");
report.Status = WorkAssignmentReportStatus.Draft;
Check(WorkReportNotificationEvents.Build(report, assignment, work, true).Count == 4, "current draft cannot erase event history");
report.LifecycleProjectionOutbox.ForEach(x => x.State = WorkReportLifecycleProjectionOutboxStates.Completed);
Check(WorkReportNotificationEvents.Build(report, assignment, work, true).Count == 4, "completed projection is still notification evidence");
var legacyKey = WorkReportNotificationEvents.LegacyKey(commands[3])!;
Check(WorkReportNotificationEvents.ExcludeAlreadyDeliveredLegacy(commands, new HashSet<string> { legacyKey }).Count == 3, "pre-upgrade approval is not duplicated");
report.LifecycleProjectionOutbox[2].CreatedAtUtc = at;
var sameTime = WorkReportNotificationEvents.Build(report, assignment, work, true);
Check(sameTime[0].EventKey != sameTime[2].EventKey, "same millisecond submissions remain distinct");
Check(WorkReportNotificationEvents.ExcludeAlreadyDeliveredLegacy(sameTime, new HashSet<string> { WorkReportNotificationEvents.LegacyKey(sameTime[0])! }).Count == 3, "one legacy key never swallows two transitions");
var otherReport = new WorkAssignmentReport { Id = "777777777777777777777777", WorkId = work.Id, WorkAssignmentId = assignment.Id,
    WorkReportPeriodId = "888888888888888888888888", AssigneeUserId = "999999999999999999999999", Status = WorkAssignmentReportStatus.Draft };
Check(WorkReportNotificationEvents.Build(otherReport, assignment, work, true).Count == 0, "unsaved transition produces no event");
report.LifecycleProjectionOutbox = [Event(5, "SUBMIT", "DRAFT", "APPROVED", at)];
var auto = WorkReportNotificationEvents.Build(report, assignment, work, true);
Check(auto.Count == 1 && auto[0].Type == "REPORT_APPROVED" && auto[0].Title.Contains("tự duyệt"), "auto approval never creates review duty");
var normalize = typeof(WorkAssignmentService).GetMethod("NormalizeDueDateUtc", BindingFlags.NonPublic | BindingFlags.Static)!;
var due = new DateTime(2026, 10, 7, 16, 59, 59, 999, DateTimeKind.Utc);
Check((DateTime?)normalize.Invoke(null, new object?[] { due }) == due, "Vietnam deadline survives backend normalization");
Check((DateTime?)normalize.Invoke(null, new object?[] { null }) is null, "optional deadline stays absent");

// Calendar dates remain dates in storage; progress compares Vietnam day boundaries
// with UTC instants, without changing the clock or any stored assignment.
var progressType = typeof(WorkAssignmentProgressService);
object? ProgressCall(string name, params object?[] values)
    => progressType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values);
var calendarAssignment = new WorkAssignment { AssignmentType = "ONCE", StartDate = new DateTime(2026, 10, 7), DueDate = new DateTime(2026, 10, 7) };
var startInstant = new DateTime(2026, 10, 6, 17, 0, 0, DateTimeKind.Utc);
var endInstant = startInstant.AddDays(1).AddTicks(-1);
Check((DateTime)ProgressCall("ResolveAssignmentStartUtc", calendarAssignment, at)! == startInstant, "progress begins at Vietnam midnight, not UTC midnight");
Check((DateTime?)ProgressCall("ResolveAssignmentEndUtc", calendarAssignment, work, null) == endInstant, "progress includes the entire Vietnam end day");
var factsType = progressType.Assembly.GetType("tdtd_be.Services.WorkAssignments.Progress.LeafProgressFacts")!;
bool IsOpenAt(DateTime clock)
{
    var facts = Activator.CreateInstance(factsType)!;
    ProgressCall("ApplyMaterializedPeriodFacts", facts, new List<WorkReportPeriod> {
        new() { Status = WorkReportPeriodStatus.Approved, DueAtUtc = due }
    }, calendarAssignment, work, null, clock);
    return (bool)factsType.GetProperty("HasAnyOpenPeriod")!.GetValue(facts)!;
}
Check(!IsOpenAt(startInstant.AddTicks(-1)), "progress is not open before the business start day");
Check(IsOpenAt(startInstant), "progress opens exactly at the business start day");
Check(IsOpenAt(endInstant), "approved report does not end the assignment before its end boundary");
Check(!IsOpenAt(endInstant.AddTicks(1)), "progress closes after the business end day");
var instantOnly = new WorkAssignment { AssignmentType = "ONCE", DueAtUtc = due };
Check((DateTime?)ProgressCall("ResolveAssignmentEndUtc", instantOnly, work, null) == due, "UTC deadline is never converted twice");
Check((DateTime)ProgressCall("ResolveAssignmentStartUtc", instantOnly, startInstant.AddHours(1))! == startInstant, "missing start uses the current Vietnam calendar day");
calendarAssignment.Schedule = new AssignmentSchedule { StartDate = new DateTime(2026, 10, 8) };
Check((DateTime)ProgressCall("ResolveAssignmentStartDay", calendarAssignment, at)! == new DateTime(2026, 10, 8), "later schedule start retains its calendar key");
Check((DateTime)ProgressCall("ResolveAssignmentStartUtc", calendarAssignment, at)! == startInstant.AddDays(1), "later schedule start is converted once at comparison");
Console.WriteLine($"{checks} checks passed; no database or runtime mutations.");

if (args.Contains("--deadline-read-query"))
{
    // Execute the actual product $set expression using inline $documents.
    // No fixtures/collections are inserted, no jobs run and no clock is changed.
    var cases = new[] {
        ("before-window", at.AddHours(24).AddMilliseconds(1), true, "OPEN"),
        ("at-window", at.AddHours(24), true, "DUE_SOON"),
        ("inside-window", at.AddHours(12), true, "DUE_SOON"),
        ("at-deadline", at, true, "DUE_SOON"),
        ("after-deadline", at.AddMilliseconds(-1), true, "OVERDUE"),
        ("submitted-no-submit-duty", at.AddHours(-1), false, "RESOLVED"),
        ("approved-no-submit-duty", at.AddHours(12), false, "RESOLVED")
    };
    var rows = new BsonArray(cases.Select(x => new BsonDocument {
        { "name", x.Item1 }, { "dueAtUtc", new BsonDateTime(x.Item2) },
        { "requiresAction", x.Item3 }, { "function", "REPORT" }
    }));
    var stages = WorkInboxPipeline.Build(ObjectId.Parse(report.AssigneeUserId), "a", "w", "p", "c", "r", "l", "t", at, 24, true);
    var command = new BsonDocument {
        { "aggregate", 1 }, { "pipeline", new BsonArray { new BsonDocument("$documents", rows), stages.Last() } },
        { "cursor", new BsonDocument() }
    };
    var db = new MongoClient("mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000").GetDatabase("tdtd");
    var result = await db.RunCommandAsync<BsonDocument>(command);
    var actual = result["cursor"]["firstBatch"].AsBsonArray.Select(x => x.AsBsonDocument).ToList();
    foreach (var item in cases) Check(actual.Single(x => x["name"] == item.Item1)["state"] == item.Item4, "real BSON deadline expression: " + item.Item1);
    Console.WriteLine($"{checks} total checks passed; collectionless read query only, no stored data changed.");
}

if (args.Contains("--uat-completion-audit"))
{
    var ctx = new MongoDbContext(Options.Create(new MongoOptions {
        ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
    var reportIds = new[] { "6ac47adde237dd6def37b707", "6ac47b51e237dd6def37b748", "6ac48f018b193ca620881c66" };
    var finalReports = await ctx.WorkAssignmentReports.Find(x => reportIds.Contains(x.Id)).ToListAsync();
    var finalNotices = await ctx.Notifications.Find(x => !x.IsDeleted && reportIds.Contains(x.WorkAssignmentReportId!)).ToListAsync();
    Console.WriteLine("READ_ONLY_COMPLETION " + JsonSerializer.Serialize(new {
        AtUtc = DateTime.UtcNow,
        Reports = finalReports.Select(x => new { x.Id, x.Status, x.PayloadRevision, x.LifecycleRevision, x.DueAtUtc,
            Events = x.LifecycleProjectionOutbox.Select(e => new { e.CommandId, e.Operation, e.LifecycleRevision, e.State }) }),
        Notifications = finalNotices.GroupBy(x => new { x.WorkAssignmentReportId, x.Type, x.RecipientUserId })
            .Select(g => new { g.Key, Count = g.Count(), DistinctEvents = g.Select(x => x.EventKey).Distinct().Count() })
    }));
}

if (args.Contains("--uat-read-audit"))
{
    var ctx = new MongoDbContext(Options.Create(new MongoOptions {
        ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
    var ids = new[] { "6ac413bf5e204bc3cab29dee", "6ac470a1066462d646de278a", "6ac47adde237dd6def37b707", "6ac47b51e237dd6def37b748" };
    var reports = await ctx.WorkAssignmentReports.Find(x => ids.Contains(x.Id)).ToListAsync();
    var notices = await ctx.Notifications.Find(x => !x.IsDeleted && ids.Contains(x.WorkAssignmentReportId!)).ToListAsync();
    var works = new[] { "6ac40f855e204bc3cab298b5", "6ac47a3ee237dd6def37b498", "6ac47e95e237dd6def37be7b" };
    var assignments = await ctx.WorkAssignments.Find(x => works.Contains(x.WorkId) && !x.IsDeleted).ToListAsync();
    var periods = await ctx.WorkReportPeriods.Find(x => works.Contains(x.WorkId) && !x.IsDeleted).ToListAsync();
    var utcEnd = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray {
        new BsonDocument("$dateToString", new BsonDocument { { "date", "$dueAtUtc" }, { "format", "%H:%M:%S.%L" }, { "timezone", "UTC" }, { "onNull", "" } }), "23:59:59.999" }));
    var allCandidateAssignments = await ctx.WorkAssignments.CountDocumentsAsync(utcEnd);
    var allCandidatePeriods = await ctx.WorkReportPeriods.CountDocumentsAsync(utcEnd);
    var allCandidateReports = await ctx.WorkAssignmentReports.CountDocumentsAsync(utcEnd);
    Console.WriteLine("READ_ONLY_UAT_AUDIT " + JsonSerializer.Serialize(new {
        AtUtc = DateTime.UtcNow,
        Reports = reports.Select(x => new { x.Id, x.AssigneeUserId, x.Status, x.PayloadRevision, x.LifecycleRevision, x.DueAtUtc,
            Events = x.LifecycleProjectionOutbox.Select(e => new { e.LifecycleRevision, e.Operation, e.CreatedAtUtc, e.State }) }),
        Notifications = notices.GroupBy(x => new { x.WorkAssignmentReportId, x.Type, x.RecipientUserId })
            .Select(g => new { g.Key, Count = g.Count(), DistinctEvents = g.Select(x => x.EventKey).Distinct().Count() }),
        Assignments = assignments.Select(x => new { x.Id, x.WorkId, x.Name, x.StartDate, x.DueAtUtc, x.ProgressStatus }),
        Periods = periods.Select(x => new { x.Id, x.WorkId, x.WorkAssignmentId, x.AssigneeUserId, x.Status, x.DueAtUtc }),
        LegacyUtcEndCandidates = new { Assignments = allCandidateAssignments, Periods = allCandidatePeriods, Reports = allCandidateReports }
    }));
}
