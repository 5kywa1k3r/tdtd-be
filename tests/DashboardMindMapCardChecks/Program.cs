using System.Text.Json;
using tdtd_be.DashboardModel.DTOs.MindMap;
using tdtd_be.DashboardModel.Services;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

var checks = new List<string>();
void Check(string name, bool value) { if (!value) throw new Exception(name); checks.Add(name); }
var node = new WorkAssignment { Id = "a", WorkId = "w", AssignmentType = WorkAssignmentTypes.PeriodicReport, Schedule = new AssignmentSchedule { CycleType = "MONTHLY" } };
WorkTemplateAssignee Binding(string user, string? unit, string form = "f") => new() { WorkId = "w", WorkAssignmentId = "a", IsActive = true, AssigneeUserId = user, AssigneeUnitId = unit, DynamicFormTemplateId = form };
WorkReportPeriod Period(string id, string? reportId, WorkReportPeriodStatus status = WorkReportPeriodStatus.Pending, string user = "u1", string form = "f") => new() { Id = id, WorkId = "w", WorkAssignmentId = "a", IsActive = true, AssigneeUserId = user, DynamicFormTemplateId = form, CurrentReportId = reportId, Status = status };
WorkAssignmentReport Report(WorkReportPeriod period, WorkAssignmentReportStatus status) => new() { Id = period.CurrentReportId!, WorkId = "w", WorkAssignmentId = "a", WorkReportPeriodId = period.Id, AssigneeUserId = period.AssigneeUserId, DynamicFormTemplateId = period.DynamicFormTemplateId, IsActive = true, IsCurrent = true, Status = status };
var bindings = new[] { Binding("u1", "unit1"), Binding("u2", "unit1"), Binding("u3", "unit2"), Binding("u4", null), Binding("u1", "unit1"), Binding("u1", null) };
var p1 = Period("p1", "r1", WorkReportPeriodStatus.OverdueApproved);
var p2 = Period("p2", "r2", WorkReportPeriodStatus.Submitted);
var p3 = Period("p3", "r3", WorkReportPeriodStatus.Draft);
var p4 = Period("p4", null);
var p5 = Period("p5", "bad");
var reports = new[] { Report(p1, WorkAssignmentReportStatus.Approved), Report(p2, WorkAssignmentReportStatus.Submitted), Report(p3, WorkAssignmentReportStatus.Draft) };
reports[2].ReturnedAtUtc = DateTime.UtcNow;
var row = DashboardMindMapCardProjection.Build(node, bindings, new[] { p1, p2, p3, p4, p5 }, reports.ToDictionary(r => r.Id));
var form = row.Forms.Single();
Check("distinct units separate from recipients", row.RecipientUnitCount == 2 && row.RecipientUserCount == 4);
Check("unknown unit users distinct and known wins", row.UnknownRecipientUnitCount == 1 && form.UnknownRecipientUnitCount == 1);
Check("monthly source schedule", row.ScheduleKind == "PERIODIC_REPORT" && row.ScheduleLabel == "Định kỳ · Hằng tháng");
Check("materialized denominator", form.PeriodCount == 5);
Check("valid actual current reports only", form.ReportedCount == 3);
Check("submitted approved separate", form.SubmittedCount == 1 && form.ApprovedCount == 1);
Check("returned draft only", form.ReturnedCount == 1);
Check("missing pointer only", form.MissingCount == 1);
Check("invalid pointer unavailable not missing", form.UnavailableCurrentReportCount == 1 && form.ReadState == "PARTIAL" && row.ReadState == "PARTIAL");
Check("overdue includes approved late separate axis", form.OverdueCount == 1 && form.ApprovedCount == 1);
Check("undated periods explicit", form.NoDueDateCount == 5);
Check("period count partitions reported missing unavailable", form.PeriodCount == form.ReportedCount + form.MissingCount + form.UnavailableCurrentReportCount);
var wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.WorkReportPeriodId = "other";
Check("foreign period fails closed", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.WorkId = "other";
Check("foreign work fails closed", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.WorkAssignmentId = "other";
Check("foreign assignment fails closed", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.DynamicFormTemplateId = "other";
Check("foreign form fails closed", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.AssigneeUserId = "other";
Check("foreign recipient fails closed", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.IsCurrent = false;
Check("history rejected", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p1, WorkAssignmentReportStatus.Approved); wrong.IsActive = false;
Check("inactive report rejected", !DashboardMindMapCardProjection.Matches(wrong, p1));
wrong = Report(p3, WorkAssignmentReportStatus.Submitted); wrong.ReturnedAtUtc = DateTime.UtcNow;
Check("resubmission is not currently returned", DashboardMindMapCardProjection.Build(node, bindings, new[] { p3 }, new Dictionary<string, WorkAssignmentReport> { [wrong.Id] = wrong }).Forms.Single().ReturnedCount == 0);
var descendant = Binding("foreign", "unit3"); descendant.WorkAssignmentId = "child";
var inactive = Binding("inactive", "unit4"); inactive.IsActive = false;
var isolated = DashboardMindMapCardProjection.Build(node, new[] { bindings[0], descendant, inactive }, Array.Empty<WorkReportPeriod>(), new Dictionary<string, WorkAssignmentReport>());
Check("direct assignment only, inactive excluded", isolated.RecipientUserCount == 1 && isolated.RecipientUnitCount == 1);
Check("unmaterialized schedule creates no expected missing", isolated.Forms.Single().PeriodCount == 0 && isolated.Forms.Single().MissingCount == 0);
var historical = DashboardMindMapCardProjection.Build(node, Array.Empty<WorkTemplateAssignee>(), new[] { p1 }, reports.ToDictionary(r => r.Id));
Check("historical periods do not inflate active recipients", historical.RecipientUserCount == 0 && historical.RecipientUnitCount == 0 && historical.Forms.Single().PeriodCount == 1);
node.AssignmentType = WorkAssignmentTypes.Once;
Check("once metadata independent of generated periods", DashboardMindMapCardProjection.Schedule(node) == ("ONCE", "Một lần"));
var listPeriod = Period("list-period", "list-report", WorkReportPeriodStatus.OverdueApproved);
listPeriod.LastSubmittedAtUtc = DateTime.UtcNow.AddDays(-2);
listPeriod.LastReviewedAtUtc = DateTime.UtcNow.AddDays(-1);
var listReport = Report(listPeriod, WorkAssignmentReportStatus.Approved);
listReport.SubmittedAtUtc = DateTime.UtcNow.AddDays(-3);
listReport.ApprovedAtUtc = DateTime.UtcNow;
var listRow = DashboardMindMapQueryService.MapTemplateReportRow(listPeriod, node, bindings[0], listReport);
Check("list available uses actual current report status", listRow.CurrentReportReadState == "AVAILABLE" && listRow.ReportId == listReport.Id && listRow.ReportStatus == (int)WorkAssignmentReportStatus.Approved);
Check("list available uses report timestamps", listRow.SubmittedAtUtc == listReport.SubmittedAtUtc && listRow.ApprovedAtUtc == listReport.ApprovedAtUtc);
var unavailable = DashboardMindMapQueryService.MapTemplateReportRow(listPeriod, node, null, null);
Check("list unresolved pointer unavailable not missing", unavailable.CurrentReportReadState == "UNAVAILABLE" && unavailable.ReportId is null && unavailable.ReportStatus is null);
Check("list period approval cannot infer report status or timestamps", unavailable.PeriodStatus == (int)WorkReportPeriodStatus.OverdueApproved && unavailable.SubmittedAtUtc is null && unavailable.ApprovedAtUtc is null);
var invalidPointer = Period("invalid-pointer-period", "not-an-object-id", WorkReportPeriodStatus.Approved);
Check("list invalid pointer unavailable", DashboardMindMapQueryService.MapTemplateReportRow(invalidPointer, node, null, null).CurrentReportReadState == "UNAVAILABLE");
foreach (var pointer in new string?[] { null, "", " " })
{
    var missing = DashboardMindMapQueryService.MapTemplateReportRow(Period("missing-period", pointer, WorkReportPeriodStatus.Approved), node, null, listReport);
    Check("list empty pointer missing " + (pointer is null ? "null" : pointer.Length), missing.CurrentReportReadState == "MISSING" && missing.ReportId is null && missing.ReportStatus is null);
}
foreach (var (name, mutate) in new (string, Action<WorkAssignmentReport>)[] {
    ("foreign work", r => r.WorkId = "other"), ("foreign assignment", r => r.WorkAssignmentId = "other"),
    ("foreign period", r => r.WorkReportPeriodId = "other"), ("foreign form", r => r.DynamicFormTemplateId = "other"),
    ("foreign recipient", r => r.AssigneeUserId = "other"), ("foreign report id", r => r.Id = "other"),
    ("deleted", r => r.IsDeleted = true), ("historical", r => r.IsCurrent = false), ("inactive", r => r.IsActive = false),
    ("unsupported status", r => r.Status = (WorkAssignmentReportStatus)99) })
{
    var candidate = Report(listPeriod, WorkAssignmentReportStatus.Approved);
    mutate(candidate);
    var guarded = DashboardMindMapQueryService.MapTemplateReportRow(listPeriod, node, null, candidate);
    Check("list " + name + " report suppressed", guarded.CurrentReportReadState == "UNAVAILABLE" && guarded.ReportId is null && guarded.ReportStatus is null && guarded.SubmittedAtUtc is null && guarded.ApprovedAtUtc is null);
}
Check("legacy row read state remains nullable", new DashboardMindMapReportRowDto().CurrentReportReadState is null);
var id = "507f1f77bcf86cd799439011";
Check("duplicates normalized", DashboardMindMapQueryService.ValidateCardSummaryRequest(id, new() { AssignmentIds = [id, id.ToUpperInvariant()] }).Count == 1);
foreach (var (name, request) in new (string, DashboardMindMapCardSummariesRequest)[] {
    ("empty batch rejected", new()), ("over50 rejected", new() { AssignmentIds = Enumerable.Repeat(id, 51).ToList() }),
    ("invalid id rejected", new() { AssignmentIds = ["bad"] }),
    ("partial date range rejected", new() { AssignmentIds = [id], FromUtc = DateTime.UtcNow }),
    ("reversed range rejected", new() { AssignmentIds = [id], FromUtc = DateTime.UtcNow, ToUtc = DateTime.UtcNow.AddDays(-1) }),
    ("timezone missing rejected", new() { AssignmentIds = [id], FromUtc = new DateTime(2026, 10, 1), ToUtc = new DateTime(2026, 10, 31) }),
    ("invalid unit rejected", new() { AssignmentIds = [id], UnitIds = ["bad"] }) })
{
    bool rejected = false; try { DashboardMindMapQueryService.ValidateCardSummaryRequest(id, request); } catch { rejected = true; }
    Check(name, rejected);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
