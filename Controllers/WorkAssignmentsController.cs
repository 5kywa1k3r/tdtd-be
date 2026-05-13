using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.WorkAssignments.Handover;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class WorkAssignmentsController : ControllerBase
{
    private readonly IWorkAssignmentService _service;
    private readonly IWorkAssignmentHandoverService _handover;

    public WorkAssignmentsController(
        IWorkAssignmentService service,
        IWorkAssignmentHandoverService handover)
    {
        _service = service;
        _handover = handover;
    }

    [HttpGet("works/{workId}/assignments")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetByWorkId(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetByWorkIdAsync(workId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("works/{workId}/my-report-assignments")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetMyReportAssignments(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetMyReportAssignmentsAsync(workId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("works/{workId}/my-review-parent-assignments")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetMyReviewParentAssignments(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetMyReviewParentAssignmentsAsync(workId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("works/{workId}/assignment-parent-candidates")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetMyParentCandidates(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetMyParentCandidatesAsync(workId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignments/{id}")]
    public async Task<ActionResult<WorkAssignmentResponse>> GetById(
        [FromRoute] string id,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetByIdAsync(id, actorUserId, ct);
        if (rs is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, new { assignmentId = id });

        return Ok(rs);
    }

    [HttpGet("work-assignments/{parentAssignmentId}/children")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetChildren(
        [FromRoute] string parentAssignmentId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetChildrenAsync(parentAssignmentId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("works/{workId}/assignments")]
    public async Task<ActionResult<WorkAssignmentResponse>> Create(
        [FromRoute] string workId,
        [FromBody] SaveWorkAssignmentRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.CreateAsync(workId, req, actorUserId, ct);
        return CreatedAtAction(nameof(GetById), new { id = rs.Id }, rs);
    }

    [HttpPatch("work-assignments/{id}/dynamic-form-data-source-rules")]
    public async Task<ActionResult<WorkAssignmentResponse>> UpdateDataSourceRules(
        [FromRoute] string id,
        [FromBody] UpdateWorkAssignmentDataSourceRulesRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.UpdateDataSourceRulesAsync(id, req, actorUserId, ct);
        if (rs is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, new { assignmentId = id });

        return Ok(rs);
    }

    [HttpPost("work-assignments/{id}/deactivate")]
    public async Task<ActionResult> Deactivate(
        [FromRoute] string id,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var ok = await _service.DeactivateAsync(id, actorUserId, ct);
        if (!ok)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, new { assignmentId = id });

        return NoContent();
    }

    [HttpPost("work-assignments/{id}/activate")]
    public async Task<ActionResult> Activate(
        [FromRoute] string id,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var ok = await _service.ActivateAsync(id, actorUserId, ct);
        if (!ok)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, new { assignmentId = id });

        return NoContent();
    }

    [HttpPost("work-assignments/{id}/handover")]
    public async Task<ActionResult<WorkAssignmentHandoverResponse>> Handover(
        [FromRoute] string id,
        [FromBody] HandoverWorkAssignmentRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _handover.HandoverAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("works/{workId}/assignment-handovers")]
    public async Task<ActionResult<PagedResult<WorkAssignmentHandoverHistoryRow>>> SearchHandoverHistory(
        [FromRoute] string workId,
        [FromBody] WorkAssignmentHandoverHistorySearchRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _handover.SearchHistoryAsync(workId, req, actorUserId, ct);
        return Ok(rs);
    }

    private string GetActorUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirstValue("sub")
               ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.AUTH_ME_NOT_AVAILABLE);
    }
}
