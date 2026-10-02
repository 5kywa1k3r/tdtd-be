using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.Services.WorkAssignments;

internal static class AssignmentActivityControllerChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var service = DispatchProxy.Create<IWorkAssignmentService, ActivityServiceProxy>();
        var proxy = (ActivityServiceProxy)(object)service;
        var controller = new WorkAssignmentsController(service, null!) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "creator") }, "test"))
            } }
        };
        using var cancellation = new CancellationTokenSource();
        check(await controller.Deactivate("assignment", cancellation.Token) is NoContentResult &&
            proxy.Method == "DeactivateAsync" && Equals(proxy.Args![0], "assignment") && Equals(proxy.Args[1], "creator") &&
            Equals(proxy.Args[2], cancellation.Token), "deactivate route delegates actor/id/cancellation to existing service");
        check(await controller.Activate("assignment", cancellation.Token) is NoContentResult && proxy.Method == "ActivateAsync",
            "activate route delegates to existing service");
        proxy.Result = false;
        foreach (var activate in new[] { false, true }) {
            try {
                if (activate) await controller.Activate("missing-or-not-owned", default);
                else await controller.Deactivate("missing-or-not-owned", default);
                throw new Exception("Expected not-found");
            } catch (AppException ex) {
                check(ex.Descriptor.Code == AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, "service false remains not-found: " + (activate ? "activate" : "deactivate"));
            }
        }
        proxy.Error = AppExceptionFactory.Create(AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE);
        try { await controller.Deactivate("flow", default); throw new Exception("Expected phase guard"); }
        catch (AppException ex) { check(ReferenceEquals(ex, proxy.Error), "route preserves service phase/permission failures"); }
        proxy.Method = null;
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        try { await controller.Deactivate("assignment", default); throw new Exception("Expected unauthorized"); }
        catch (AppException ex) { check(ex.Descriptor.Code == AppErrorCode.AUTH_ME_NOT_AVAILABLE && proxy.Method is null, "missing actor rejected before service"); }
    }
}

public class ActivityServiceProxy : DispatchProxy
{
    public bool Result = true;
    public Exception? Error;
    public string? Method;
    public object?[]? Args;
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        Method = method!.Name;
        Args = args;
        return Error is null ? Task.FromResult(Result) : Task.FromException<bool>(Error);
    }
}
