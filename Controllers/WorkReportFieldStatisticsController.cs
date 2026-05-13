using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/work-report-field-statistics")]
public sealed class WorkReportFieldStatisticsController : ControllerBase
{
    private readonly IWorkReportFieldStatisticsService _service;
    private readonly MeAccessor _me;

    public WorkReportFieldStatisticsController(
        IWorkReportFieldStatisticsService service,
        MeAccessor me)
    {
        _service = service;
        _me = me;
    }

    [HttpPost("summary")]
    public async Task<ActionResult<FieldStatisticSummaryResponse>> Summary(
        [FromBody] FieldStatisticSummaryRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchSummaryAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("text-concat")]
    public async Task<ActionResult<FieldTextConcatResponse>> TextConcat(
        [FromBody] FieldTextConcatRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchTextConcatAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("text-concat/export")]
    public async Task<IActionResult> ExportTextConcatCsv(
        [FromBody] FieldTextConcatRequest req,
        CancellationToken ct)
    {
        var file = await _service.ExportTextConcatCsvAsync(req, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("rebuild")]
    public async Task<ActionResult<RebuildFieldStatisticResponse>> Rebuild(
        [FromBody] RebuildFieldStatisticRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var result = await _service.RebuildForWorkPeriodAsync(req, me.Id, ct);
        return Ok(result);
    }
}
