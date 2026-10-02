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

// 30/09/2026: giữ mã cũ, chặn toàn bộ entry point; dùng Aggregate v2 trong báo cáo.
[tdtd_be.Services.WorkAssignments.LegacyAggregateDisabled]
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

    [HttpPost("native-summary")]
    public async Task<IActionResult> NativeSummary([FromBody] JsonElement body, CancellationToken ct)
    {
        _candidateActivation.RequireCapability(StatRunCapabilities.BasicSummary, StatRunRouteRegistry.BasicResult);
        return Ok(await _service.GetNativeSummaryAsync(StatConfigCanonicalJson.DeserializeStrict<BasicNativeSummaryRequest>(body), ct));
    }

    [HttpPost("native-refresh")]
    public async Task<IActionResult> NativeRefresh([FromBody] JsonElement body, CancellationToken ct)
        => Ok(await _service.QueueNativeRefreshAsync(StatConfigCanonicalJson.DeserializeStrict<BasicNativeSummaryRequest>(body), ct));

    [HttpGet("native-refresh/by-command/{commandId}")]
    public async Task<IActionResult> NativeRefreshByCommand([FromRoute] string commandId, CancellationToken ct)
        => Ok(await _service.ReadNativeRefreshByCommandAsync(commandId, ct));

    [HttpGet("native-refresh/{refreshId}")]
    public async Task<IActionResult> NativeRefreshStatus([FromRoute] string refreshId, CancellationToken ct)
        => Ok(await _service.ReadNativeRefreshAsync(refreshId, ct));

    [HttpPost("native-refresh/{refreshId}/retry")]
    public async Task<IActionResult> RetryNativeRefresh([FromRoute] string refreshId, CancellationToken ct)
        => Ok(await _service.RetryNativeRefreshAsync(refreshId, ct));

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
