using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Aggregate;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Block(Action action, string name) {
    try { action(); throw new Exception("NOT BLOCKED: " + name); }
    catch (AppException ex) { Check(ex.Details?.ToString()?.Contains(LegacyAggregateRetirement.Reason) == true, name); }
}
var resource = new ResourceExecutingContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
    new List<IFilterMetadata>(), new List<IValueProviderFactory>());
new LegacyAggregateDisabledAttribute().OnResourceExecuting(resource);
Check(resource.Result is ObjectResult {StatusCode: 410}, "resource filter stops before action and returns 410");
Check(((ObjectResult)resource.Result!).Value!.ToString()!.Contains("LEGACY_AGGREGATE_DISABLED"), "stable retirement reason");

foreach (var type in new[] {typeof(WorkAssignmentBasicSummaryController),typeof(WorkAssignmentAdvancedSummaryController),typeof(WorkAssignmentAggregateTableController)})
    Check(type.IsDefined(typeof(LegacyAggregateDisabledAttribute)), "all routes blocked: "+type.Name);
foreach (var type in new[] {typeof(WorkAssignmentsController),typeof(WorkAssignmentReportsController),typeof(AdminOperationsController)}) {
    foreach (var method in type.GetMethods().Where(m=>m.GetCustomAttributes<HttpMethodAttribute>().Any(a =>
        a.Template?.Contains("dynamic-form-data-source-rules") == true ||
        a.Template?.Contains("dynamic-form-aggregate") == true ||
        a.Template?.Contains("basic-summary-jobs") == true || a.Template?.Contains("advanced-summary-nodes") == true)))
        Check(method.IsDefined(typeof(LegacyAggregateDisabledAttribute)), "mixed route blocked: "+method.Name);
}
var v2 = typeof(WorkAssignmentsController).Assembly.GetTypes().Where(t=>t.GetCustomAttributes<RouteAttribute>().Any(a=>a.Template.Contains("aggregate-v2"))).ToArray();
Check(v2.Length>0, "v2 controllers discovered");
foreach(var type in v2) Check(!type.IsDefined(typeof(LegacyAggregateDisabledAttribute)) && !type.GetMethods().Any(m=>m.IsDefined(typeof(LegacyAggregateDisabledAttribute))), "v2 unaffected: "+type.Name);
var noop = new HashSet<string> {"RefreshSnapshotJobAsync","RefreshNativeSnapshotJobAsync","RunPreviewJobAsync","BuildDayNodeJobAsync","BuildMonthNodeJobAsync","BuildYearNodeJobAsync","MarkReportStatusMutationDirtyAsync","MarkApprovedReportPayloadDirtyAsync","RefreshDynamicFormAggregateDependentsAsync","RecoverPendingAsync"};
foreach(var type in new[]{typeof(WorkAssignmentBasicSummaryService),typeof(WorkAssignmentAdvancedSummaryConfigService),
    typeof(WorkAssignmentAdvancedSummaryHierarchyService),typeof(WorkAssignmentAdvancedSummaryDirtyService),typeof(AggregateTableService),
    typeof(WorkAssignmentService),typeof(WorkAssignmentReportService),typeof(WorkReportAggregateDependentRecoveryService)}) {
    var instance = RuntimeHelpers.GetUninitializedObject(type); // All dependencies null: any I/O/access before guard fails.
    var methods = type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>typeof(Task).IsAssignableFrom(m.ReturnType));
    if(type==typeof(WorkAssignmentService)) methods=methods.Where(m=>m.Name=="UpdateDataSourceRulesAsync");
    if(type==typeof(WorkAssignmentReportService)) methods=methods.Where(m=>m.Name is "ApplyDynamicFormAggregateDraftAsync" or "PreviewDynamicFormAggregateDraftAsync" or "RefreshDynamicFormAggregateDependentsAsync");
    methods=methods.Where(m=>m.Name != "ReadSourcesPageAsync"); // P10 audit read owner is outside product retirement.
    foreach(var method in methods) {
        var callArgs=method.GetParameters().Select(p=>p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        Exception? failure=null;
        try { await (Task)method.Invoke(instance,callArgs)!; }
        catch(TargetInvocationException ex){failure=ex.InnerException;}
        catch(Exception ex){failure=ex;}
        if(noop.Contains(method.Name)) Check(failure is null, "queued job/hook no I/O: "+type.Name+"."+method.Name);
        else Check(failure is AppException app && app.Details?.ToString()?.Contains(LegacyAggregateRetirement.Reason)==true,
            "service blocked before I/O: "+type.Name+"."+method.Name+" "+failure?.GetType().Name);
    }
}
foreach(var mode in new[]{"MAP_CHILD","MIXED","AGGREGATE_CHILDREN"})
    Block(()=>LegacyAggregateRetirement.RequireManualSourceRules("{\"sectionRules\":[{\"sourceRule\":\""+mode+"\"}]}"), "create rejects "+mode);
Block(()=>LegacyAggregateRetirement.RequireManualSourceRules("{\"fieldRules\":[{\"sourceRule\":\"MANUAL\",\"sourceAssignmentId\":\"hidden\"}]}"), "create rejects hidden source pins");
LegacyAggregateRetirement.RequireManualSourceRules(null);
LegacyAggregateRetirement.RequireManualSourceRules("{\"sectionRules\":[{\"sectionId\":\"s\",\"sourceRule\":\"MANUAL\",\"sourceAssignmentIds\":[]}]}");
Check(true,"ordinary manual assignment supported");
var old="{\"kind\":\"DYNAMIC_FORM_AGGREGATE_DRAFT\",\"aggregateRequest\":{}}";
Block(()=>LegacyAggregateRetirement.RequireUnchangedLegacySummary(old,null),"save DTO cannot inject old aggregate recipe");
Block(()=>LegacyAggregateRetirement.RequireUnchangedLegacySummary(old.Replace("{}","{\"changed\":true}"),old),"save DTO cannot change old recipe");
LegacyAggregateRetirement.RequireUnchangedLegacySummary(null,old);
LegacyAggregateRetirement.RequireUnchangedLegacySummary(old,old);
LegacyAggregateRetirement.RequireUnchangedLegacySummary("{\"kind\":\"DYNAMIC_FLOW_MAPPING\"}",null);
Check(true,"old readback unchanged/null and separate Flow contract preserved");
var reportService = RuntimeHelpers.GetUninitializedObject(typeof(WorkAssignmentReportService));
var storedReport = new tdtd_be.Models.WorkAssignmentReport { Id = "fixture", AggregateSnapshotDirty = true, SummarySourceJson = old };
foreach (var name in new[] { "RefreshAggregateSnapshotForReadAsync", "RefreshDynamicFormAggregateReportFromSummaryAsync" }) {
    var method = typeof(WorkAssignmentReportService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    var callArgs = method.GetParameters().Select(p => p.ParameterType == typeof(tdtd_be.Models.WorkAssignmentReport) ? storedReport : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
    var task = (Task)method.Invoke(reportService, callArgs)!;
    await task;
    Check(ReferenceEquals(task.GetType().GetProperty("Result")!.GetValue(task), storedReport) && storedReport.AggregateSnapshotDirty && storedReport.SummarySourceJson == old,
        "legacy lazy refresh preserves stored report without I/O: " + name);
}
Console.WriteLine($"TOTAL {passed} checks passed; no database, product host or browser UAT.");

