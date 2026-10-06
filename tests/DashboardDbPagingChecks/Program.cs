using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;
using tdtd_be.DashboardModel.Services;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

// Literal $documents aggregation only: no fixture insertion, writes, indexes or seed.
var database = new MongoClient("mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000").GetDatabase("tdtd");
var now = new DateTime(2026, 10, 7, 11, 0, 0, DateTimeKind.Utc); var today = now.Date;
var workId = ObjectId.GenerateNewId().ToString(); var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
async Task<List<BsonDocument>> Project(List<BsonDocument> docs, string kind) {
    var command = new BsonDocument { { "aggregate", 1 }, { "pipeline", new BsonArray {
        new BsonDocument("$documents", new BsonArray(docs)), new BsonDocument("$project", DashboardLeadershipDbProjection.Item(kind, now)) } }, { "cursor", new BsonDocument("batchSize", 2000) } };
    return (await database.RunCommandAsync<BsonDocument>(command))["cursor"]["firstBatch"].AsBsonArray.Select(x => x.AsBsonDocument).ToList();
}
var works = new List<Work>();
foreach (var status in new[] { 1, 2, 3, 4, 5 }) foreach (int? delta in new int?[] { null, -5, 0, 1, 7, 8, 30, 31, 365 }) foreach (var fallback in new[] { false, true }) {
    var due = delta.HasValue ? today.AddDays(delta.Value) : (DateTime?)null;
    works.Add(new Work { Id = ObjectId.GenerateNewId().ToString(), AutoCode = "W", Name = "Work", Status = (WorkStatus)status, Priority = WorkPriority.MEDIUM,
        DueDate = fallback ? null : due, EndDate = fallback ? due : null, CompletedDate = status == 3 ? today : null });
}
var projected = await Project(works.Select(x => x.ToBsonDocument()).ToList(), "WORK");
foreach (var pair in works.Zip(projected)) {
    var expected = DashboardLeadershipProjection.WorkItem(pair.First, Array.Empty<WorkAssignment>(), today);
    Check(pair.Second["Timeliness"] == expected.Timeliness, "Work timing contract");
    Check(pair.Second["DueDate"] == (BsonValue?)expected.DueDate || pair.Second["DueDate"].IsBsonNull && expected.DueDate is null, "Work fallback deadline");
}
foreach (var key in DashboardLeadershipProjection.DeadlineBucketKeys) {
    var request = new DashboardLeadershipItemsRequest { Context = new() { View = "LEADERSHIP", Section = "CURRENT" }, Selection = "WORK_DEADLINE_" + key };
    var pipeline = new BsonArray { new BsonDocument("$documents", new BsonArray(works.Select(x => x.ToBsonDocument()))),
        new BsonDocument("$match", DashboardLeadershipDbProjection.WorkPrefilter(request.Selection, today)),
        new BsonDocument("$project", DashboardLeadershipDbProjection.Item("WORK", now)),
        new BsonDocument("$match", DashboardLeadershipDbProjection.Selection(request, request.Selection, today, today, today)), DashboardLeadershipDbProjection.Page(1, 2) };
    var result = (await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "aggregate", 1 }, { "pipeline", pipeline }, { "cursor", new BsonDocument() } }))["cursor"]["firstBatch"][0];
    var expected = works.Count(x => DashboardLeadershipProjection.DeadlineBucket(x, today) == key);
    Check(result["Total"][0]["count"].ToInt32() == expected, "DB count matches deadline projection: " + key);
    Check(result["Rows"].AsBsonArray.Count == Math.Min(2, Math.Max(0, expected - 2)), "DB skip/limit: " + key);
}
var assignments = new List<WorkAssignment>();
foreach (var status in new[] { 0, 1, 2, 3, 4 }) foreach (int? delta in new int?[] { null, -1, 0, 1 }) foreach (var completedAt in new[] { false, true }) {
    assignments.Add(new WorkAssignment { Id = ObjectId.GenerateNewId().ToString(), WorkId = workId, RootAssignmentId = workId, Code = "A", Name = "Assignment", Path = "/a", ProgressStatus = status,
        DueAtUtc = delta.HasValue ? today.AddHours(-7).AddDays(delta.Value) : null, CompletedAtUtc = completedAt ? now : null, CompletedDate = status == 2 ? today : null });
}
var work = works[0].ToBsonDocument();
var assignmentDocs = assignments.Select(x => { var doc = x.ToBsonDocument(); doc["__work"] = work; return doc; }).ToList();
var assignmentRows = await Project(assignmentDocs, "ASSIGNMENT");
foreach (var pair in assignments.Zip(assignmentRows)) Check(pair.Second["Timeliness"] == DashboardLeadershipProjection.AssignmentTiming(pair.First, today), "Assignment timing enum/UTC contract");
var reports = new List<(WorkAssignmentReport Report, WorkReportPeriod Period)>();
foreach (var status in new[] { WorkAssignmentReportStatus.Draft, WorkAssignmentReportStatus.Submitted, WorkAssignmentReportStatus.Approved })
foreach (var returned in new[] { false, true }) foreach (int? submitted in new int?[] { null, -1, 0, 1 }) foreach (var late in new[] { false, true }) foreach (var noDue in new[] { false, true }) {
    var due = noDue ? (DateTime?)null : now.AddHours(-1);
    reports.Add((new WorkAssignmentReport { Id = ObjectId.GenerateNewId().ToString(), Status = status, ReturnedAtUtc = returned ? now : null, SubmittedAtUtc = submitted.HasValue ? (due ?? now).AddSeconds(submitted.Value) : null, DueAtUtc = due, IsLateSubmission = late }, new WorkReportPeriod { Id = ObjectId.GenerateNewId().ToString(), DueAtUtc = due }));
}
var reportDocs = reports.Select(pair => new BsonDocument { { "_id", ObjectId.Parse(pair.Period.Id) }, { "dueAtUtc", pair.Period.DueAtUtc is {} due ? new BsonDateTime(due) : BsonNull.Value },
    { "__work", work }, { "__assignment", assignmentDocs[0] }, { "__report", new BsonDocument { { "_id", ObjectId.Parse(pair.Report.Id) }, { "status", (int)pair.Report.Status },
        { "dueAtUtc", pair.Report.DueAtUtc is {} d ? new BsonDateTime(d) : BsonNull.Value }, { "submittedAtUtc", pair.Report.SubmittedAtUtc is {} s ? new BsonDateTime(s) : BsonNull.Value },
        { "returnedAtUtc", pair.Report.ReturnedAtUtc is {} r ? new BsonDateTime(r) : BsonNull.Value }, { "isLateSubmission", pair.Report.IsLateSubmission } } }, { "__candidates", new BsonArray() } }).ToList();
var reportRows = await Project(reportDocs, "REPORT");
foreach (var pair in reports.Zip(reportRows)) { var state = DashboardLeadershipProjection.ReportState(pair.First.Report, pair.First.Period, now); Check(pair.Second["ReportStatus"] == state.Status && pair.Second["Timeliness"] == state.Timeliness, "Report status/submission deadline contract"); }
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, literalMongoDocumentsOnly = true, databaseWrites = false }));
