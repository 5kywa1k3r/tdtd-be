using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/work-report-statistic-diffs")]
public sealed class WorkReportStatisticDiffController : ControllerBase
{
    private readonly IWorkReportStatisticDiffService _service;
    private readonly MeAccessor _me;

    public WorkReportStatisticDiffController(
        IWorkReportStatisticDiffService service,
        MeAccessor me)
    {
        _service = service;
        _me = me;
    }

    [HttpGet("configs")]
    public async Task<ActionResult<List<WorkReportStatisticDiffConfigDto>>> ListConfigs(
        [FromQuery] string? workId,
        [FromQuery] string? assignmentId,
        [FromQuery] string? dynamicFormTemplateId,
        CancellationToken ct)
    {
        var result = await _service.ListConfigsAsync(workId, assignmentId, dynamicFormTemplateId, ct);
        return Ok(result);
    }

    [HttpPost("configs")]
    public async Task<ActionResult<WorkReportStatisticDiffConfigDto>> SaveConfig(
        [FromBody] WorkReportStatisticDiffSaveRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var result = await _service.SaveConfigAsync(req, me.Id, ct);
        return Ok(result);
    }

    [HttpDelete("configs/{configId}")]
    public async Task<IActionResult> DeleteConfig(string configId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        await _service.DeleteConfigAsync(configId, me.Id, ct);
        return NoContent();
    }

    [HttpPost("run")]
    public async Task<ActionResult<WorkReportStatisticDiffRunResponse>> Run(
        [FromBody] WorkReportStatisticDiffRunRequest req,
        CancellationToken ct)
    {
        var result = await _service.RunAsync(req, ct);
        return Ok(result);
    }
}
