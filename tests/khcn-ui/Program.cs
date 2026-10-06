using System;
using System.Reflection;
using tdtd_be.DashboardModel.Services;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignments.Review;

var passed = 0;
void Equal<T>(string name, T actual, T expected) {
    if (!Equals(actual, expected)) throw new Exception($"FAIL {name}: {actual} != {expected}");
    Console.WriteLine($"PASS {name}"); passed++;
}
DateTime Utc(string day) => DateTime.SpecifyKind(DateTime.Parse(day), DateTimeKind.Utc);
var bound=Utc("2026-10-31");
Equal("KUI-01 WA5 once precedence",DashboardAssignmentDeadline.Day("ONCE",Utc("2026-09-30T16:59:59.999"),bound),Utc("2026-09-30"));
Equal("KUI-01 WA6 once precedence",DashboardAssignmentDeadline.Day("ONCE",Utc("2026-10-14T16:59:59.999"),bound),Utc("2026-10-14"));
Equal("KUI-01 WA1 once precedence",DashboardAssignmentDeadline.Day("ONCE",Utc("2026-10-30T16:59:59.999"),bound),Utc("2026-10-30"));
Equal("KUI-01 before Vietnam midnight",DashboardAssignmentDeadline.Day("ONCE",Utc("2026-09-30T16:59:59"),null),Utc("2026-09-30"));
Equal("KUI-01 Vietnam midnight",DashboardAssignmentDeadline.Day("ONCE",Utc("2026-09-30T17:00:00"),null),Utc("2026-10-01"));
Equal("KUI-01 periodic boundary ignores period instant",DashboardAssignmentDeadline.Day("PERIODIC_REPORT",Utc("2026-09-29T16:59:59"),bound),bound);
Equal("KUI-01 contracted assignment bound",DashboardAssignmentDeadline.Day("PERIODIC_REPORT",null,Utc("2026-10-14")),Utc("2026-10-14"));
Equal("KUI-01 legacy stored bound fallback",DashboardAssignmentDeadline.Day("ONCE",null,bound),bound);
Equal("KUI-01 no deadline",DashboardAssignmentDeadline.Day("ONCE",null,null),(DateTime?)null);
Equal("KUI-01 no periodic deadline",DashboardAssignmentDeadline.Day("PERIODIC_REPORT",Utc("2026-10-01"),null),(DateTime?)null);

var periodMapper=typeof(WorkAssignmentReportService).GetMethod("MapToPeriodRow",BindingFlags.NonPublic|BindingFlags.Static)!;
WorkReportPeriodRow Row(bool periodActive,bool assignmentActive,bool bindingActive) =>
    (WorkReportPeriodRow)periodMapper.Invoke(null,new object?[]{new WorkReportPeriod{Id="period",IsActive=periodActive,PeriodKey="20260930"},
        new WorkAssignment{Id="assignment",IsActive=assignmentActive,AssignmentType="ONCE"},Utc("2026-10-07"),bindingActive})!;
Equal("KUI-04 active period",Row(true,true,true).IsActive,true);
Equal("KUI-04 stopped assignment",Row(true,false,true).IsActive,false);
Equal("KUI-04 stopped period",Row(false,true,true).IsActive,false);
Equal("KUI-04 stopped binding",Row(true,true,false).IsActive,false);
Equal("KUI-04 retains historical period key",Row(false,false,false).PeriodKey,"20260930");

var summaryMapper=typeof(WorkAssignmentReviewService).GetMethod("MapToReviewSummaryRow",BindingFlags.NonPublic|BindingFlags.Static)!;
var projection=new ReviewAssignmentSummaryDocRole{AssignmentId="a",DynamicFormTemplateId="projection-pin",DynamicFormTemplateName="Projection name",DynamicExcelName="Excel"};
var assignment=new WorkAssignment{Id="a",DynamicFormTemplateId="assigned-pin",DynamicFormFamilyId="family",DynamicFormTemplateName="M01 assigned",DynamicFormTemplateCode="FORM-M01",DynamicFormVersionNo=2,DynamicFormSchemaHash="pinned-hash"};
var summary=(ReviewSummaryRowDto)summaryMapper.Invoke(null,new object?[]{projection,assignment})!;
Equal("KUI-06 assigned Form pin",summary.DynamicFormTemplateId,"assigned-pin");
Equal("KUI-06 assigned name",summary.DynamicFormTemplateName,"M01 assigned");
Equal("KUI-06 assigned version",summary.DynamicFormVersionNo,2);
Equal("KUI-06 assigned schema hash",summary.DynamicFormSchemaHash,"pinned-hash");
Equal("KUI-06 legacy Excel preserved",summary.DynamicExcelName,"Excel");
Console.WriteLine($"{passed} isolated checks passed; no database or HTTP calls.");
