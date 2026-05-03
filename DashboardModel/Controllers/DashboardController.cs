using Microsoft.AspNetCore.Mvc;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.DashboardModel.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardQueryService _dashboard;
    private readonly IDashboardOverviewService _overview;

    public DashboardController(
        IDashboardQueryService dashboard,
        IDashboardOverviewService overview)
    {
        _dashboard = dashboard;
        _overview = overview;
    }

    [HttpPost("overview")]
    public async Task<ActionResult<DashboardOverviewResponse>> GetOverview(
        [FromBody] DashboardOverviewRequest? req,
        CancellationToken ct)
    {
        var result = await _overview.GetOverviewAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("overview/refresh")]
    public async Task<ActionResult<DashboardOverviewResponse>> RefreshOverview(
        [FromBody] DashboardOverviewRequest? req,
        CancellationToken ct)
    {
        req ??= new DashboardOverviewRequest();
        req.ForceRefresh = true;

        var result = await _overview.GetOverviewAsync(req, ct);
        return Ok(result);
    }

    [HttpGet("report-assignment-options")]
    public async Task<ActionResult<List<DashboardReportAssignmentOptionDto>>> GetReportAssignmentOptions(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] List<string>? unitIds,
        CancellationToken ct = default)
    {
        var req = new DashboardReportAssignmentOptionsRequest
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UnitIds = unitIds?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>()
        };

        var result = await _overview.GetReportAssignmentOptionsAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("my-works/summary")]
    public async Task<ActionResult<MyWorksDashboardResponse>> GetMyWorksSummary(
        [FromBody] MyWorksDashboardRequest? req,
        CancellationToken ct)
    {
        var request = req ?? new MyWorksDashboardRequest();
        var result = await _dashboard.GetMyWorksSummaryAsync(request, ct);
        return Ok(result);
    }

    [HttpPost("my-works/summary/refresh")]
    public async Task<ActionResult<MyWorksDashboardResponse>> RefreshMyWorksSummary(
        [FromBody] MyWorksDashboardRequest? req,
        CancellationToken ct)
    {
        var request = req ?? new MyWorksDashboardRequest();
        request.ForceRefresh = true;

        var result = await _dashboard.GetMyWorksSummaryAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("works/{workId}")]
    public async Task<ActionResult<WorkDashboardDetailDto>> GetWorkDetail(
        [FromRoute] string workId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] List<string>? unitIds,
        [FromQuery] bool includeRootAssignments = true,
        [FromQuery] bool includeReportSummary = true,
        [FromQuery] bool? forceRefresh = null,
        CancellationToken ct = default)
    {
        var req = BuildWorkDetailRequest(
            fromUtc,
            toUtc,
            unitIds,
            includeRootAssignments,
            includeReportSummary,
            forceRefresh ?? false);

        var result = await _dashboard.GetWorkDetailAsync(workId, req, ct);
        return Ok(result);
    }

    [HttpGet("works/{workId}/refresh")]
    public async Task<ActionResult<WorkDashboardDetailDto>> RefreshWorkDetail(
        [FromRoute] string workId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] List<string>? unitIds,
        [FromQuery] bool includeRootAssignments = true,
        [FromQuery] bool includeReportSummary = true,
        CancellationToken ct = default)
    {
        var req = BuildWorkDetailRequest(
            fromUtc,
            toUtc,
            unitIds,
            includeRootAssignments,
            includeReportSummary,
            forceRefresh: true);

        var result = await _dashboard.GetWorkDetailAsync(workId, req, ct);
        return Ok(result);
    }

    private static WorkDashboardDetailRequest BuildWorkDetailRequest(
        DateTime? fromUtc,
        DateTime? toUtc,
        List<string>? unitIds,
        bool includeRootAssignments,
        bool includeReportSummary,
        bool forceRefresh)
    {
        return new WorkDashboardDetailRequest
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UnitIds = unitIds?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>(),
            IncludeRootAssignments = includeRootAssignments,
            IncludeReportSummary = includeReportSummary,
            ForceRefresh = forceRefresh
        };
    }
}
