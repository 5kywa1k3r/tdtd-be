using Microsoft.AspNetCore.Mvc;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.DashboardModel.DTOs.MindMap;
using tdtd_be.DashboardModel.Services;
using tdtd_be.DTOs.Common;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/dashboard-mindmap")]
public sealed class DashboardMindMapController : ControllerBase
{
    private readonly IDashboardMindMapQueryService _service;

    public DashboardMindMapController(IDashboardMindMapQueryService service)
    {
        _service = service;
    }

    [HttpGet("works/{workId}")]
    public async Task<ActionResult<DashboardMindMapWorkResponse>> GetWorkTree(
        [FromRoute] string workId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] List<string>? unitIds,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _service.GetWorkTreeAsync(
            workId,
            BuildScopeRequest(fromUtc, toUtc, unitIds),
            page,
            pageSize,
            ct);
        return Ok(result);
    }

    [HttpGet("works/{workId}/root-assignments")]
    public async Task<ActionResult<DashboardMindMapCursorResult<DashboardTreeNodeDto>>> GetRootAssignments(
        [FromRoute] string workId,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var result = await _service.GetRootAssignmentsAsync(workId, cursor, limit, ct);
        return Ok(result);
    }

    [HttpGet("nodes/{assignmentId}/children")]
    public async Task<ActionResult<DashboardMindMapCursorResult<DashboardTreeNodeDto>>> GetChildren(
        [FromRoute] string assignmentId,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var result = await _service.SearchChildrenCursorAsync(assignmentId, cursor, limit, ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/children/search")]
    public async Task<ActionResult<PagedResult<DashboardTreeNodeDto>>> SearchChildren(
        [FromRoute] string assignmentId,
        [FromBody] DashboardMindMapNodeChildrenSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchChildrenAsync(assignmentId, req, ct);
        return Ok(result);
    }

    [HttpGet("nodes/{assignmentId}/template-groups")]
    public async Task<ActionResult<List<DashboardMindMapTemplateGroupDto>>> SearchTemplateGroups(
        [FromRoute] string assignmentId,
        CancellationToken ct = default)
    {
        var result = await _service.SearchTemplateGroupsAsync(assignmentId, ct);
        return Ok(result);
    }

    [HttpGet("nodes/{assignmentId}/forms/{dynamicFormTemplateId}/users")]
    public async Task<ActionResult<DashboardMindMapCursorResult<DashboardMindMapTemplateUserDto>>> SearchTemplateUsers(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromQuery] string? q,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 5,
        CancellationToken ct = default)
    {
        var result = await _service.SearchTemplateUsersAsync(
            assignmentId,
            dynamicFormTemplateId,
            q,
            cursor,
            limit,
            ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/forms/{dynamicFormTemplateId}/reports/search")]
    public async Task<ActionResult<DashboardMindMapCursorResult<DashboardMindMapReportRowDto>>> SearchTemplateReports(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromBody] DashboardMindMapTemplateReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchTemplateReportsAsync(assignmentId, dynamicFormTemplateId, req, ct);
        return Ok(result);
    }

    [HttpGet("nodes/{assignmentId}/summary")]
    public async Task<ActionResult<DashboardMindMapNodeSummaryDto>> GetNodeSummary(
        [FromRoute] string assignmentId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] List<string>? unitIds,
        CancellationToken ct = default)
    {
        var result = await _service.GetNodeSummaryAsync(
            assignmentId,
            BuildScopeRequest(fromUtc, toUtc, unitIds),
            ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/units/search")]
    public async Task<ActionResult<PagedResult<DashboardMindMapUnitRowDto>>> SearchNodeUnits(
        [FromRoute] string assignmentId,
        [FromBody] DashboardMindMapNodeUnitsSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchNodeUnitsAsync(assignmentId, req, ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/reports/search")]
    public async Task<ActionResult<PagedResult<DashboardMindMapReportRowDto>>> SearchNodeReports(
        [FromRoute] string assignmentId,
        [FromBody] DashboardMindMapNodeReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchNodeReportsAsync(assignmentId, req, ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/table-metrics/reports/search")]
    public async Task<ActionResult<PagedResult<DashboardMindMapTableMetricReportRowDto>>> SearchNodeTableMetricReports(
        [FromRoute] string assignmentId,
        [FromBody] DashboardMindMapTableMetricReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchNodeTableMetricReportsAsync(assignmentId, req, ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/field-metrics/reports/search")]
    public async Task<ActionResult<PagedResult<DashboardMindMapFieldMetricReportRowDto>>> SearchNodeFieldMetricReports(
        [FromRoute] string assignmentId,
        [FromBody] DashboardMindMapFieldMetricReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchNodeFieldMetricReportsAsync(assignmentId, req, ct);
        return Ok(result);
    }

    [HttpPost("nodes/{assignmentId}/labels/reports/search")]
    public async Task<ActionResult<PagedResult<DashboardMindMapLabelReportRowDto>>> SearchNodeLabelReports(
        [FromRoute] string assignmentId,
        [FromBody] DashboardMindMapLabelReportsSearchRequest? req,
        CancellationToken ct = default)
    {
        var result = await _service.SearchNodeLabelReportsAsync(assignmentId, req, ct);
        return Ok(result);
    }

    private static DashboardMindMapScopeRequest BuildScopeRequest(
        DateTime? fromUtc,
        DateTime? toUtc,
        List<string>? unitIds)
    {
        return new DashboardMindMapScopeRequest
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UnitIds = unitIds?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>(),
        };
    }
}
