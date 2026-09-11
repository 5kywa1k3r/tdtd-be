using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-report-table-statistics")]
public sealed class WorkReportTableStatisticsController : ControllerBase
{
    private readonly IWorkReportTableStatisticsService _service;
    private readonly MeAccessor _me;
    private readonly IP9DirectResultService _p9Results;
    private readonly IStatRunCandidateActivation _activation;

    public WorkReportTableStatisticsController(
        IWorkReportTableStatisticsService service,
        MeAccessor me,
        IP9DirectResultService p9Results,
        IStatRunCandidateActivation activation)
    {
        _service = service;
        _me = me;
        _p9Results = p9Results;
        _activation = activation;
    }

    [HttpPost("summary")]
    public async Task<ActionResult<TableStatisticSummaryResponse>> Summary(
        [FromBody] TableStatisticSummaryRequest req,
        CancellationToken ct)
    {
        _activation.RequireCapability(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.DirectTableResult);
        var result = await _p9Results.ReadTableAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("rebuild")]
    public async Task<ActionResult<RebuildTableStatisticResponse>> Rebuild(
        [FromBody] RebuildTableStatisticRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Projection);
        var result = await _service.RebuildForWorkPeriodAsync(req, me.Id, ct);
        return Ok(result);
    }
}
