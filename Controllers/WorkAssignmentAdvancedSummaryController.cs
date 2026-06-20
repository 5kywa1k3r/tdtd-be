using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-assignment-advanced-summary")]
public sealed class WorkAssignmentAdvancedSummaryController : ControllerBase
{
    private readonly IWorkAssignmentAdvancedSummaryConfigService _configs;

    public WorkAssignmentAdvancedSummaryController(IWorkAssignmentAdvancedSummaryConfigService configs)
    {
        _configs = configs;
    }

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/configs")]
    public async Task<IActionResult> ListConfigs(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _configs.ListConfigsAsync(assignmentId, dynamicFormTemplateId, sectionId, actorUserId, ct);
        return Ok(result);
    }

    [HttpPut("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/draft")]
    public async Task<IActionResult> SaveDraft(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromBody] SaveWorkAssignmentAdvancedSummaryDraftRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _configs.SaveDraftAsync(assignmentId, dynamicFormTemplateId, sectionId, req, actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("configs/{configId}/lock")]
    public async Task<IActionResult> LockConfig(
        [FromRoute] string configId,
        [FromBody] LockWorkAssignmentAdvancedSummaryConfigRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _configs.LockConfigAsync(configId, req, actorUserId, ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
