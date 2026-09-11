using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-assignment-basic-summary")]
public sealed class WorkAssignmentBasicSummaryController : ControllerBase
{
    private readonly IWorkAssignmentBasicSummaryService _service;
    private readonly IStatRunCandidateActivation _candidateActivation;

    public WorkAssignmentBasicSummaryController(
        IWorkAssignmentBasicSummaryService service,
        IStatRunCandidateActivation candidateActivation)
    {
        _service = service;
        _candidateActivation = candidateActivation;
    }

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config")]
    public async Task<IActionResult> GetConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        CancellationToken ct)
    {
        var result = await _service.GetP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct);
        return Ok(result);
    }

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions")]
    public async Task<IActionResult> ListConfigVersions(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        CancellationToken ct)
        => Ok(await _service.ListP8ConfigVersionsAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions/{versionNo:int}")]
    public async Task<IActionResult> GetConfigVersion(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] int versionNo,
        CancellationToken ct)
        => Ok(await _service.GetP8ConfigVersionAsync(
            assignmentId,
            dynamicFormTemplateId,
            versionNo,
            ct));

    [HttpPut("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config")]
    public async Task<IActionResult> PutConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _service.PutP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            body,
            ct));

    [HttpPost("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/lock")]
    public async Task<IActionResult> LockConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _service.LockP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            body,
            ct));

    [HttpPost("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/next-draft")]
    public async Task<IActionResult> CreateNextDraft(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _service.CreateNextP8DraftAsync(
            assignmentId,
            dynamicFormTemplateId,
            body,
            ct));

    [HttpPost("summary")]
    public async Task<IActionResult> Summary(
        [FromBody] WorkAssignmentBasicSummaryRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.BasicSummary,
            StatRunRouteRegistry.BasicResult);
        var actorUserId = GetActorUserId();
        var result = await _service.GetSummaryAsync(req, actorUserId, ct);
        return Ok(result);
    }

    [HttpPost("once")]
    public async Task<IActionResult> Once(
        [FromBody] WorkAssignmentBasicSummaryRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.BasicSummary,
            StatRunRouteRegistry.BasicResult);
        var actorUserId = GetActorUserId();
        var result = await _service.GetSummaryAsync(req, actorUserId, ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
