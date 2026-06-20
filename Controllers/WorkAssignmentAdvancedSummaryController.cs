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
    private readonly IWorkAssignmentAdvancedSummaryHierarchyService _hierarchy;

    public WorkAssignmentAdvancedSummaryController(
        IWorkAssignmentAdvancedSummaryConfigService configs,
        IWorkAssignmentAdvancedSummaryHierarchyService hierarchy)
    {
        _configs = configs;
        _hierarchy = hierarchy;
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

    [HttpPost("configs/{configId}/preview")]
    public async Task<IActionResult> RequestPreview(
        [FromRoute] string configId,
        [FromBody] PreviewWorkAssignmentAdvancedSummaryConfigRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _configs.RequestPreviewAsync(configId, req ?? new PreviewWorkAssignmentAdvancedSummaryConfigRequest(), actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("configs/{configId}/hierarchy/day/{dayKey}/build")]
    public async Task<IActionResult> BuildDayNode(
        [FromRoute] string configId,
        [FromRoute] string dayKey,
        [FromBody] BuildWorkAssignmentAdvancedSummaryDayNodeRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _hierarchy.RequestDayNodeBuildAsync(configId, dayKey, req ?? new BuildWorkAssignmentAdvancedSummaryDayNodeRequest(), actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("configs/{configId}/hierarchy/month/{monthKey}/build")]
    public async Task<IActionResult> BuildMonthNode(
        [FromRoute] string configId,
        [FromRoute] string monthKey,
        [FromBody] BuildWorkAssignmentAdvancedSummaryMonthNodeRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _hierarchy.RequestMonthNodeBuildAsync(configId, monthKey, req ?? new BuildWorkAssignmentAdvancedSummaryMonthNodeRequest(), actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("configs/{configId}/hierarchy/year/{yearKey}/build")]
    public async Task<IActionResult> BuildYearNode(
        [FromRoute] string configId,
        [FromRoute] string yearKey,
        [FromBody] BuildWorkAssignmentAdvancedSummaryYearNodeRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _hierarchy.RequestYearNodeBuildAsync(configId, yearKey, req ?? new BuildWorkAssignmentAdvancedSummaryYearNodeRequest(), actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("configs/{configId}/hierarchy/query")]
    public async Task<IActionResult> QueryHierarchy(
        [FromRoute] string configId,
        [FromBody] QueryWorkAssignmentAdvancedSummaryHierarchyRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var result = await _hierarchy.QueryHierarchyAsync(configId, req ?? new QueryWorkAssignmentAdvancedSummaryHierarchyRequest(), actorUserId, ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
