using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/work-report-table-statistics")]
public sealed class WorkReportTableStatisticsController : ControllerBase
{
    private readonly IWorkReportTableStatisticsService _service;
    private readonly MeAccessor _me;

    public WorkReportTableStatisticsController(
        IWorkReportTableStatisticsService service,
        MeAccessor me)
    {
        _service = service;
        _me = me;
    }

    [HttpPost("summary")]
    public async Task<ActionResult<TableStatisticSummaryResponse>> Summary(
        [FromBody] TableStatisticSummaryRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchSummaryAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("rebuild")]
    public async Task<ActionResult<RebuildTableStatisticResponse>> Rebuild(
        [FromBody] RebuildTableStatisticRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var result = await _service.RebuildForWorkPeriodAsync(req, me.Id, ct);
        return Ok(result);
    }
}
