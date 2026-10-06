using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Time;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.AggregateMapping;

static DateTime U(int day, int hour = 0) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);
static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
var assignment = new WorkAssignment { StartDate = U(4), CreatedAtUtc = U(6, 18) };
var resolve = typeof(WorkAssignmentReportService).GetMethod("ResolveReportCompletedDatePolicy", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var day in new[] { 4, 5, 6, 7 })
{
    var legacy = U(day - 1, 17);
    var period = new WorkReportPeriod { PeriodStart = legacy, PeriodEnd = legacy, ReportDate = legacy, DueAtUtc = U(day), PeriodKey = $"202610{day:00}" };
    var policy = resolve.Invoke(null, new object?[] { assignment, null, period, U(6, 20) })!;
    object? Get(string name) => policy.GetType().GetProperty(name)!.GetValue(policy);
    Check((bool)Get("CanEditCompletedDate")! == (day < 7), $"legacy period {day}: edit classification");
    Check((bool)Get("RequiresCompletedDate")! == (day < 7), $"legacy period {day}: required classification");
    if (day < 7)
    {
        Check((DateTime)Get("CompletedDateMin")! == U(day), $"period {day}: min is civil day");
        Check((DateTime)Get("CompletedDateMax")! == U(7), $"period {day}: max is Vietnam today across UTC midnight");
    }
}
foreach (var cycle in new[] { "DAILY", "WEEKLY", "MONTHLY", "QUARTERLY", "SEMI_ANNUAL" })
{
    var (start, end) = SchedulePeriodHelper.GetPeriodRange(cycle, new DateTime(2026, 10, 4));
    var bson = new WorkReportPeriod { PeriodStart = start, PeriodEnd = end }.ToBson();
    var read = BsonSerializer.Deserialize<WorkReportPeriod>(bson);
    Check(start.Kind == DateTimeKind.Utc && end.Kind == DateTimeKind.Utc && read.PeriodStart == start && read.PeriodEnd == end, cycle + " BSON roundtrip calendar days");
}
var completed = WorkAssignmentReportHistoricalDataHelper.NormalizeDate(new DateTime(2026, 10, 4));
var stored = BsonSerializer.Deserialize<WorkReportPeriod>(new WorkReportPeriod { CompletedDate = completed }.ToBson());
Check(stored.CompletedDate == U(4), "completion input BSON roundtrip");
Check(!WorkAssignmentReportHistoricalDataHelper.ResolveIsLateSubmission(true, U(4), U(3, 22), U(7)), "completion Oct4 vs deadline Oct4 05:00 Vietnam is same day");
Check(WorkAssignmentReportHistoricalDataHelper.ResolveIsLateSubmission(true, U(5), U(3, 22), U(7)), "completion next civil day is late");
Check(ReportCivilDate.ReadPeriodDay(U(4)) == U(4), "date-only marker unchanged");
Check(ReportCivilDate.ReadPeriodDay(U(3, 17)) == U(4), "legacy local midnight read without migration");
Check(WorkAssignmentBackfillPeriodPolicy.TryResolveCompletedDateBounds(assignment,U(4),U(4),U(4),U(7),out var min,out var max)&&min==U(4)&&max==U(7), "new marker uses same bounds");
foreach (var field in new[] { "PERIOD_START", "PERIOD_END" })
{
    AggregateSourceHeader Header(DateTime value) => new(null!, "parent", null!, "unit", "20261004", true, true, false, null) { PeriodStart = value, PeriodEnd = value };
    var filter = new tdtd_be.DTOs.AggregateMapping.AggregateReportSetDto(1, "AND", [new(field, "RANGE", ["2026-10-04", "2026-10-07"])]);
    Check(AggregateReportSetFilter.Matches(filter, Header(U(3,17)), null!) == true, field + " includes legacy Oct4 midnight in Oct4-7 range");
    Check(AggregateReportSetFilter.Matches(filter, Header(U(4)), null!) == true, field + " includes new date-only marker");
    Check(AggregateReportSetFilter.Matches(filter, Header(U(7,17)), null!) == false, field + " excludes Oct8 without expanding filter");
}
