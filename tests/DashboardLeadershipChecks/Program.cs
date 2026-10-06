using System.Text.Json;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.DashboardModel.Services;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
void Throws(Action action, string name) { try { action(); } catch { checks.Add(name); return; } throw new Exception(name); }
DateTime D(int year, int month, int day) => new(year, month, day);
var now = new DateTime(2026, 10, 6, 5, 0, 0, DateTimeKind.Utc);
var today = DashboardLeadershipProjection.BusinessToday(now);
var works = new List<Work> {
    new() { Id = "long", Name = "Chương trình chuyển đổi số năm 2026", Type = WorkType.TASK, Status = WorkStatus.S2, Priority = WorkPriority.HIGH, StartDate = D(2026,1,1), DueDate = D(2026,12,31), CreatedAtUtc = D(2025,12,20) },
    new() { Id = "no-due", Name = "Theo dõi chỉ tiêu thường xuyên", Type = WorkType.INDICATOR, Status = WorkStatus.S2, Priority = WorkPriority.MEDIUM, StartDate = D(2026,3,1), CreatedAtUtc = D(2026,3,1) },
    new() { Id = "old-late", Name = "Xử lý tồn từ tháng trước", Type = WorkType.TASK, Status = WorkStatus.S2, Priority = WorkPriority.HIGH, StartDate = D(2026,9,1), DueDate = D(2026,9,28), CreatedAtUtc = D(2026,9,1) },
    new() { Id = "risk", Name = "Chỉ tiêu có cảnh báo", Type = WorkType.INDICATOR, Status = WorkStatus.S4, Priority = WorkPriority.MEDIUM, StartDate = D(2026,10,1), DueDate = D(2026,10,20), CreatedAtUtc = D(2026,10,1) },
    new() { Id = "done", Name = "Nhiệm vụ hoàn thành trong tháng", Type = WorkType.TASK, Status = WorkStatus.S3, Priority = WorkPriority.LOW, DueDate = D(2026,10,8), CompletedDate = D(2026,10,2), CreatedAtUtc = D(2025,1,1) },
    new() { Id = "done-missing", Name = "Hoàn thành thiếu ngày", Type = WorkType.TASK, Status = WorkStatus.S3, Priority = WorkPriority.LOW, CreatedAtUtc = D(2025,1,1) },
    new() { Id = "due-today", Name = "Hạn hôm nay", Type = WorkType.TASK, Status = WorkStatus.S1, Priority = WorkPriority.LOW, DueDate = D(2026,10,6), CreatedAtUtc = new DateTime(2026,9,30,17,0,0,DateTimeKind.Utc) },
    new() { Id = "no-dates", Name = "Việc lâu không cập nhật, thiếu ngày", Type = WorkType.INDICATOR, Status = WorkStatus.S1, Priority = WorkPriority.LOW, CreatedAtUtc = D(2020,1,1) },
};
var assignments = new List<WorkAssignment> {
    new() { Id="entry", WorkId="long", Path="/hidden/entry", ParentAssignmentId="hidden", Name="Nhánh được giao", ProgressStatus=2, CompletedDate=D(2026,10,3), DueDate=D(2026,10,10), CreatedAtUtc=D(2026,10,1), IsActive=true },
    new() { Id="child", WorkId="long", Path="/hidden/entry/child", ParentAssignmentId="entry", Name="Công việc con", ProgressStatus=1, DueDate=D(2026,10,4), CreatedAtUtc=D(2026,10,2), IsActive=true },
    new() { Id="other-entry", WorkId="long", Path="/other", ProgressStatus=0, DueDate=D(2026,10,30), CreatedAtUtc=D(2026,10,3), IsActive=true },
};
var current = DashboardLeadershipProjection.Current(works, assignments, now);
Check(current.TotalWorkCount == 8 && current.TotalOpenWorkCount == 6, "total Work includes completed; tracking stays open only");
Check(current.WorkStatus.Sum(s=>s.Count)==8 && current.AssignmentStatus.Sum(s=>s.Count)==3, "source status denominators are independent");
Check(current.InProgressWorkCount == 3 && current.OverdueWorkCount == 1, "raw in-progress may overlap derived overdue");
Check(current.WorkStatus.Single(s=>s.Key=="5").Count==0, "deadline does not rewrite Work source enum");
Check(current.NoDeadlineWorkCount==2 && current.OverdueBeforeWindowCount==1, "undated and old overdue stay visible");
Check(current.DeadlineBuckets.Select(b=>b.Key).SequenceEqual(DashboardLeadershipProjection.DeadlineBucketKeys) && current.DeadlineBuckets.Sum(b=>b.Count)==current.TotalOpenWorkCount,
    "deadline chart has six ordered buckets partitioning all readable open Work");
Check(current.DeadlineBuckets.Single(b=>b.Key=="AFTER_30_DAYS").Count==1 && current.DeadlineBuckets.Single(b=>b.Key=="NO_DEADLINE").Count==2,
    "year-long and undated Work remain in their actual deadline groups");
var emptyDeadlines = DashboardLeadershipProjection.DeadlineCounts(Array.Empty<Work>(), today);
Check(emptyDeadlines.Count==6 && emptyDeadlines.All(b=>b.Count==0), "empty scope preserves all six zero deadline rows");
foreach (var (offset, key) in new (int? Offset, string Key)[] {(-1,"OVERDUE"),(0,"TODAY"),(1,"NEXT_7_DAYS"),(7,"NEXT_7_DAYS"),(8,"NEXT_8_30_DAYS"),(30,"NEXT_8_30_DAYS"),(31,"AFTER_30_DAYS"),(null,"NO_DEADLINE")}) {
    var work = new Work {Status=WorkStatus.S2,DueDate=offset is { } days ? today.AddDays(days).AddHours(23) : null};
    Check(DashboardLeadershipProjection.DeadlineBucket(work,today)==key,"actual deadline day boundary "+(offset?.ToString()??"none"));
}
Check(DashboardLeadershipProjection.DeadlineBucket(new(){Status=WorkStatus.S5,DueDate=today.AddDays(31)},today)=="AFTER_30_DAYS" &&
    DashboardLeadershipProjection.DeadlineBucket(new(){Status=WorkStatus.S5},today)=="NO_DEADLINE", "source overdue status does not replace the actual deadline group");
Check(DashboardLeadershipProjection.DeadlineBucket(new(){Status=WorkStatus.S3,DueDate=today.AddDays(-1)},today) is null &&
    DashboardLeadershipProjection.DeadlineBucket(new(){Status=WorkStatus.S2,IsDeleted=true,DueDate=today},today) is null, "completed and deleted Work do not enter deadline groups");
Check(DashboardLeadershipProjection.DeadlineBucket(new(){Status=WorkStatus.S2,EndDate=today.AddDays(7)},today)=="NEXT_7_DAYS" &&
    DashboardLeadershipProjection.DeadlineBucket(new(){Status=WorkStatus.S2,DueDate=today.AddDays(8),EndDate=today.AddDays(-1)},today)=="NEXT_8_30_DAYS", "DueDate takes precedence and legacy EndDate remains a fallback");
Check(DashboardLeadershipProjection.BusinessToday(new DateTime(2026,10,5,16,59,59,DateTimeKind.Utc))==today.AddDays(-1) &&
    DashboardLeadershipProjection.BusinessToday(new DateTime(2026,10,5,17,0,0,DateTimeKind.Utc))==today, "deadline reference day rolls at Vietnam midnight");
var deadlineCohort = Enumerable.Range(0,66).Select(i=>new Work {Id="deadline-"+i,Status=i%5==0?WorkStatus.S5:WorkStatus.S2,
    DueDate=(i%6) switch {0=>today.AddDays(-365),1=>today,2=>today.AddDays(7),3=>today.AddDays(30),4=>today.AddDays(365),_=>(DateTime?)null}}).ToList();
deadlineCohort.Add(new Work {Id="deadline-completed",Status=WorkStatus.S3,DueDate=today.AddDays(-1)});
var deadlineCurrent = DashboardLeadershipProjection.Current(deadlineCohort,Array.Empty<WorkAssignment>(),now);
var deadlineItems = DashboardLeadershipProjection.WorkItems(deadlineCohort,Array.Empty<WorkAssignment>(),today);
Check(deadlineCurrent.WorkProgress.Count==10 && deadlineCurrent.TotalOpenWorkCount==66 && deadlineCurrent.DeadlineBuckets.All(b=>b.Count==11),
    "deadline counts cover the full 66-Work cohort independently of legacy ten-row summaries");
var selectedDeadlineIds = new HashSet<string>();
foreach(var bucket in deadlineCurrent.DeadlineBuckets) {
    var list = DashboardLeadershipProjection.DeadlineItems(deadlineCohort,deadlineItems,today,bucket.Key).ToList();
    Check(list.Count==bucket.Count && list.All(r=>selectedDeadlineIds.Add(r.Id)), "deadline chart/list agreement with no cross-bucket overlap "+bucket.Key);
    Check(list.Take(10).Count()==10 && list.Skip(10).Take(10).Count()==1, "deadline list paging retains its eleventh Work "+bucket.Key);
    DashboardLeadershipProjection.ValidateSelectionFilters(new(){Context=new(){View="LEADERSHIP",Section="CURRENT"}},"WORK_DEADLINE_"+bucket.Key);
}
Check(selectedDeadlineIds.Count==66 && !selectedDeadlineIds.Contains("deadline-completed"), "six deadline lists exhaust the open cohort exactly once");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){Context=new(){View="LEADERSHIP",Section="PERIOD"}},"WORK_DEADLINE_TODAY"),"deadline selection rejects PERIOD context");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(),"WORK_DEADLINE_UNKNOWN"),"unknown deadline group fails closed");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){Status=2},"WORK_DEADLINE_TODAY"),"deadline selection rejects status filters");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){Priority=1},"WORK_DEADLINE_TODAY"),"deadline selection rejects priority filters");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){WorkId="long"},"WORK_DEADLINE_TODAY"),"deadline selection rejects WorkId filters");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){BucketFromDate="2026-10-01"},"WORK_DEADLINE_TODAY"),"deadline selection rejects bucket start filters");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){BucketToDate="2026-10-31"},"WORK_DEADLINE_TODAY"),"deadline selection rejects bucket end filters");
Check(current.Timeline.Any(w=>w.Id=="long") && current.Timeline.Single(w=>w.Id=="long").DueDate=="2026-12-31", "long task retains its source deadline");
Check(current.WorkProgress.Single(w=>w.Id=="long").AssignmentCount==2 && current.WorkProgress.Single(w=>w.Id=="long").CompletedAssignmentCount==1, "direct progress counts readable entries, not all descendants or recipients");
Check(DashboardLeadershipProjection.WorkTiming(works.Single(w=>w.Id=="due-today"),today)=="ON_TIME", "today deadline remains on time");
Check(DashboardLeadershipProjection.WorkTiming(works.Single(w=>w.Id=="no-due"),today)=="UNASSESSED", "no deadline is not automatically on time");
Check(DashboardLeadershipProjection.WorkTiming(works.Single(w=>w.Id=="done-missing"),today)=="UNASSESSED", "missing completion evidence is unassessed");
Check(DashboardLeadershipProjection.AssignmentTiming(assignments.Single(a=>a.Id=="child"),today)=="LATE", "assignment deadline has its own timing");
Check(DashboardLeadershipProjection.AssignmentDeadline(new(){DueAtUtc=new DateTime(2026,10,5,18,0,0,DateTimeKind.Utc)})==D(2026,10,6), "assignment exact UTC deadline converts to Vietnam day");
Check(DashboardLeadershipProjection.AssignmentCompletion(new(){ProgressStatus=1,CompletedDate=D(2026,10,3)}) is null, "legacy planned CompletedDate does not become a completion event");
Check(DashboardLeadershipProjection.WorkCompletion(new(){Status=WorkStatus.S2,CompletedDate=D(2026,10,3)}) is null, "open Work with a stray date is not a completion event");
Check(DashboardLeadershipProjection.AssignmentCompletion(new(){ProgressStatus=1,CompletedAtUtc=now,CompletedDate=D(2026,10,3)})==D(2026,10,3), "confirmed completion date remains available during projection convergence");
Check(current.Timeliness.Where(s=>s.ObjectKind=="WORK").Sum(s=>s.Count)==8 && current.Timeliness.Where(s=>s.ObjectKind=="ASSIGNMENT").Sum(s=>s.Count)==3, "timing partitions each object denominator");
var activity = DashboardLeadershipProjection.Activity(D(2026,10,1),D(2026,10,31),works.Select(w=>w.CreatedAtUtc),works.Select(w=>w.CompletedDate));
Check(activity.Sum(b=>b.CreatedCount)==2 && activity.Sum(b=>b.CompletedCount)==1, "independent events include old-created completion and Vietnam UTC boundary");
Check(activity.Single(b=>b.FromDate=="2026-10-01").CreatedCount==2, "17:00 UTC belongs to the next Vietnam business day");
foreach (var (start,end) in new[] { (D(2026,1,1),D(2026,12,31)), (D(2020,2,17),D(2028,7,18)), (D(1900,1,1),D(2300,12,31)) }) {
    var bars=DashboardLeadershipProjection.Activity(start,end,new[]{start.AddDays(1),end},new DateTime?[]{end});
    Check(bars.Count<=60 && bars.First().FromDate==DashboardLeadershipProjection.Day(start) && bars.Last().ToDate==DashboardLeadershipProjection.Day(end) && bars.Sum(b=>b.CreatedCount)==2 && bars.Sum(b=>b.CompletedCount)==1,"long range retains entire span and events "+start.Year);
}
Throws(()=>DashboardLeadershipProjection.ParseDay("2026-02-30"),"invalid day fails closed");
foreach (var days in new[] { 60, 413 }) {
    var start = D(2026,1,4); var end = start.AddDays(days);
    var bars = DashboardLeadershipProjection.Activity(start,end,new[]{end},Array.Empty<DateTime?>());
    Check(bars.Count<=60 && bars.Last().ToDate==DashboardLeadershipProjection.Day(end) && bars.Sum(b=>b.CreatedCount)==1,"inclusive boundary keeps at most 60 buckets: "+days);
}
Throws(()=>DashboardLeadershipProjection.Range(new(){FromDate="2026-10-31",ToDate="2026-10-01"},now),"reversed dates fail closed");
Throws(()=>DashboardLeadershipProjection.Range(new(){FromDate="2026-10-01"},now),"one-sided range fails closed");
Throws(()=>DashboardLeadershipProjection.Section(new(){Section="ALL"}),"unknown section fails closed");
Throws(()=>DashboardLeadershipProjection.Section(new(){Section="CURRENT",UnitIds=new(){"client-unit"}}),"client unit scope cannot override authority");
Throws(()=>DashboardLeadershipProjection.Section(new(){Section="CURRENT",FromDate="2026-10-01",ToDate="2026-10-31"}),"event range cannot silently filter current state");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){Status=5},"ASSIGNMENT_STATUS"),"Work overdue enum is rejected for assignment filter");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){Status=2},"REPORT_APPROVED"),"raw numeric Work/assignment state cannot filter Report");
Throws(()=>DashboardLeadershipProjection.ValidateSelectionFilters(new(){BucketFromDate="2026-10-01"},"WORK_CREATED"),"one-sided bucket fails closed");
var period = new WorkReportPeriod {DueAtUtc=now.AddHours(-1)};
var approved = new WorkAssignmentReport {Status=WorkAssignmentReportStatus.Approved,SubmittedAtUtc=now,IsLateSubmission=true};
Check(DashboardLeadershipProjection.ReportState(approved,period,now)==("APPROVED","LATE"),"approved report may be late; never assignment completed");
Check(DashboardLeadershipProjection.ReportState(approved,new(),now)==("APPROVED","LATE"),"explicit late submission flag remains late when deadline is missing");
var returned = new WorkAssignmentReport {Status=WorkAssignmentReportStatus.Draft,ReturnedAtUtc=now};
Check(DashboardLeadershipProjection.ReportState(returned,period,now)==("RETURNED","LATE"),"returned draft is a separate display group");
returned.ReturnedAtUtc=null; returned.Status=WorkAssignmentReportStatus.Submitted; returned.SubmittedAtUtc=period.DueAtUtc;
Check(DashboardLeadershipProjection.ReportState(returned,period,now)==("SUBMITTED","ON_TIME"),"resubmission removes returned group and equality is on time");
approved.IsLateSubmission=false; approved.SubmittedAtUtc=null;
Check(DashboardLeadershipProjection.ReportState(approved,period,now)==("APPROVED","UNASSESSED"),"approved without submission evidence is not invented on time");
Check(DashboardLeadershipProjection.ReportState(null,new(){DueAtUtc=now.AddDays(1)},now)==("PENDING","UNASSESSED"),"missing report is a period obligation, not a report enum");
if(args.Length>0) File.WriteAllText(args[0],JsonSerializer.Serialize(current,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new{passed=checks.Count,checks},new JsonSerializerOptions{WriteIndented=true}));
