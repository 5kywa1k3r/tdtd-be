using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-assignment-basic-summary")]
public sealed class WorkAssignmentBasicSummaryController : ControllerBase
{
    private readonly IWorkAssignmentBasicSummaryService _service;

    public WorkAssignmentBasicSummaryController(IWorkAssignmentBasicSummaryService service)
    {
        _service = service;
    }

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config")]
    public async Task<IActionResult> GetConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _service.GetConfigAsync(assignmentId, dynamicFormTemplateId, actorUserId, ct);
        return Ok(result);
    }

    [HttpPut("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config")]
    public async Task<IActionResult> SaveConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] SaveWorkAssignmentBasicSummaryConfigRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _service.SaveConfigAsync(assignmentId, dynamicFormTemplateId, req, actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("summary")]
    public async Task<IActionResult> Summary(
        [FromBody] WorkAssignmentBasicSummaryRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _service.GetSummaryAsync(req, actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("once")]
    public async Task<IActionResult> Once(
        [FromBody] WorkAssignmentBasicSummaryRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _service.GetSummaryAsync(req, actorUserId, ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
