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
[Route("api/work-report-field-statistics")]
public sealed class WorkReportFieldStatisticsController : ControllerBase
{
    private readonly IWorkReportFieldStatisticsService _service;
    private readonly MeAccessor _me;
    private readonly IP9DirectResultService _p9Results;
    private readonly IStatRunCandidateActivation _activation;

    public WorkReportFieldStatisticsController(
        IWorkReportFieldStatisticsService service,
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
    public async Task<ActionResult<FieldStatisticSummaryResponse>> Summary(
        [FromBody] FieldStatisticSummaryRequest req,
        CancellationToken ct)
    {
        _activation.RequireCapability(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.DirectFieldResult);
        var result = await _p9Results.ReadFieldAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("text-concat")]
    public async Task<ActionResult<FieldTextConcatResponse>> TextConcat(
        [FromBody] FieldTextConcatRequest req,
        CancellationToken ct)
    {
        _activation.RequireCapability(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.DirectTextResult);
        var result = await _p9Results.ReadTextAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("text-concat/export")]
    public async Task<IActionResult> ExportTextConcatCsv(
        [FromBody] FieldTextConcatRequest req,
        CancellationToken ct)
    {
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Export);
        var file = await _service.ExportTextConcatCsvAsync(req, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("rebuild")]
    public async Task<ActionResult<RebuildFieldStatisticResponse>> Rebuild(
        [FromBody] RebuildFieldStatisticRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Projection);
        var result = await _service.RebuildForWorkPeriodAsync(req, me.Id, ct);
        return Ok(result);
    }
}
