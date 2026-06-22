using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class DynamicFlowRuntimeController : ControllerBase
{
    private readonly IDynamicFlowRuntimeService _service;

    public DynamicFlowRuntimeController(IDynamicFlowRuntimeService service)
    {
        _service = service;
    }

    [HttpPost("works/{workId}/dynamic-flows/instances")]
    public async Task<ActionResult<DynamicFlowInstanceLaunchResponse>> CreateInstance(
        [FromRoute] string workId,
        [FromBody] CreateDynamicFlowInstanceRequest req,
        CancellationToken ct)
    {
        var result = await _service.CreateInstanceAsync(
            workId,
            req ?? new CreateDynamicFlowInstanceRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/rollback")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> RollbackBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.RollbackBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/terminate")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> TerminateBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.TerminateBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/restart")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> RestartBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.RestartBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
