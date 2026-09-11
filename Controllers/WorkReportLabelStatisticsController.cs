using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-report-label-statistics")]
public sealed class WorkReportLabelStatisticsController : ControllerBase
{
    private readonly IWorkReportLabelStatisticsService _service;
    private readonly IP9DirectResultService _p9Results;
    private readonly IStatRunCandidateActivation _activation;

    public WorkReportLabelStatisticsController(
        IWorkReportLabelStatisticsService service,
        IP9DirectResultService p9Results,
        IStatRunCandidateActivation activation)
    {
        _service = service;
        _p9Results = p9Results;
        _activation = activation;
    }

    [HttpPost("summary")]
    public async Task<ActionResult<LabelStatisticSummaryResponse>> Summary(
        [FromBody] LabelStatisticSummaryRequest req,
        CancellationToken ct)
    {
        _activation.RequireCapability(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.DirectLabelResult);
        var result = await _p9Results.ReadLabelAsync(req, ct);
        return Ok(result);
    }
}
