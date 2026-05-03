using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/work-report-label-statistics")]
public sealed class WorkReportLabelStatisticsController : ControllerBase
{
    private readonly IWorkReportLabelStatisticsService _service;

    public WorkReportLabelStatisticsController(IWorkReportLabelStatisticsService service)
    {
        _service = service;
    }

    [HttpPost("summary")]
    public async Task<ActionResult<LabelStatisticSummaryResponse>> Summary(
        [FromBody] LabelStatisticSummaryRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchSummaryAsync(req, ct);
        return Ok(result);
    }
}
