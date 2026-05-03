using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Services.WorkAssignments.Review;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-assignment-review")]
public sealed class WorkAssignmentReviewController : ControllerBase
{
    private readonly IWorkAssignmentReviewService _service;

    public WorkAssignmentReviewController(IWorkAssignmentReviewService service)
    {
        _service = service;
    }

    [HttpPost("children/search")]
    public async Task<IActionResult> SearchChildren(
        [FromBody] ReviewChildSearchRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchChildrenForReviewAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("summary/search")]
    public async Task<IActionResult> SearchSummary(
        [FromBody] ReviewSummarySearchRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchSummaryForReviewAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("reports/search")]
    public async Task<IActionResult> SearchReports(
        [FromBody] ReviewReportFlatSearchRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchReportsForReviewAsync(req, ct);
        return Ok(result);
    }

    [HttpPost("reports/{reportId}/approve")]
    public async Task<IActionResult> Approve(
        [FromRoute] string reportId,
        [FromBody] ApproveReportRequest req,
        CancellationToken ct)
    {
        await _service.ApproveReportAsync(reportId, req, ct);
        return Ok();
    }

    [HttpPost("reports/{reportId}/return")]
    public async Task<IActionResult> Return(
        [FromRoute] string reportId,
        [FromBody] ReturnReportRequest req,
        CancellationToken ct)
    {
        await _service.ReturnReportAsync(reportId, req, ct);
        return Ok();
    }

    [HttpPost("reports/{reportId}/recall-approved")]
    public async Task<IActionResult> RecallApproved(
        [FromRoute] string reportId,
        [FromBody] ReturnReportRequest req,
        CancellationToken ct)
    {
        await _service.RecallApprovedReportAsync(reportId, req, ct);
        return Ok();
    }

    [HttpPost("assignments/{assignmentId}/evaluate")]
    [Authorize]
    public async Task<IActionResult> EvaluateAssignment(
    [FromRoute] string assignmentId,
    [FromBody] EvaluateAssignmentRequest req,
    CancellationToken ct)
    {
        var ok = await _service.EvaluateAssignmentAsync(assignmentId, req, ct);
        if (!ok) return NotFound();
        return Ok();
    }

    [HttpGet("assignments/{assignmentId}/evaluation-logs")]
    [Authorize]
    public async Task<ActionResult<PagedResult<WorkAssignmentEvaluationLogRow>>> GetEvaluationLogs(
    [FromRoute] string assignmentId,
    [FromQuery] int page = 0,
    [FromQuery] int pageSize = 20,
    CancellationToken ct = default)
    {
        var rs = await _service.GetEvaluationLogsAsync(assignmentId, page, pageSize, ct);
        return Ok(rs);
    }
}