using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-report-statistic-diffs")]
public sealed class WorkReportStatisticDiffController : ControllerBase
{
    private readonly IWorkReportStatisticDiffService _service;

    public WorkReportStatisticDiffController(
        IWorkReportStatisticDiffService service)
    {
        _service = service;
    }

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config")]
    public async Task<IActionResult> GetP8Config(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        CancellationToken ct)
        => Ok(await _service.GetP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions")]
    public async Task<IActionResult> ListP8ConfigVersions(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        CancellationToken ct)
        => Ok(await _service.ListP8ConfigVersionsAsync(
            assignmentId,
            dynamicFormTemplateId,
            ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions/{versionNo:int}")]
    public async Task<IActionResult> GetP8ConfigVersion(
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
    public async Task<IActionResult> PutP8Config(
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
    public async Task<IActionResult> LockP8Config(
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
    public async Task<IActionResult> CreateNextP8Draft(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _service.CreateNextP8DraftAsync(
            assignmentId,
            dynamicFormTemplateId,
            body,
            ct));

    [HttpGet("configs")]
    public IActionResult ListConfigs(
        [FromQuery] string? workId,
        [FromQuery] string? assignmentId,
        [FromQuery] string? dynamicFormTemplateId)
        => throw LegacyRouteBlocked(
            "DIFF_LEGACY_CONFIG_ROUTE_BLOCKED");

    [HttpPost("configs")]
    public IActionResult SaveConfig(
        [FromBody] JsonElement body)
        => throw LegacyRouteBlocked(
            "DIFF_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE");

    [HttpDelete("configs/{configId}")]
    public IActionResult DeleteConfig(string configId)
        => throw LegacyRouteBlocked(
            "DIFF_LEGACY_DELETE_BLOCKED_USE_CAS_CONFIG_ROUTE");

    [HttpPost("run")]
    public IActionResult Run([FromBody] JsonElement body)
    {
        StatConfigPhaseBarrier.Reject(
            StatConfigPhaseBarrierEntries.P9Run,
            "WORK_REPORT_STATISTIC_DIFF_RUN");
        StatConfigIsolationGuard.RejectResultMaterializer(
            "WORK_REPORT_STATISTIC_DIFF_RUN");
        return NoContent();
    }

    [HttpPost("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/runs")]
    public async Task<IActionResult> RunP9(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] P9StatisticDiffRunRequest request,
        CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created,
            await _service.RunP9Async(
                assignmentId,
                dynamicFormTemplateId,
                request,
                ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/results/{resultId}")]
    public async Task<IActionResult> GetP9Result(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string resultId,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
        => Ok(await _service.GetP9ResultAsync(
            assignmentId,
            dynamicFormTemplateId,
            resultId,
            page,
            pageSize,
            ct));

    private static AppException LegacyRouteBlocked(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new { reason });
}
