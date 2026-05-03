using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Services.WorkAssignments;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class WorkAssignmentsController : ControllerBase
{
    private readonly IWorkAssignmentService _service;

    public WorkAssignmentsController(IWorkAssignmentService service)
    {
        _service = service;
    }

    [HttpGet("works/{workId}/assignments")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetByWorkId(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.GetByWorkIdAsync(workId, actorUserId, ct);
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpGet("works/{workId}/my-report-assignments")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetMyReportAssignments(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.GetMyReportAssignmentsAsync(workId, actorUserId, ct);
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("works/{workId}/my-review-parent-assignments")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetMyReviewParentAssignments(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.GetMyReviewParentAssignmentsAsync(workId, actorUserId, ct);
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("works/{workId}/assignment-parent-candidates")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetMyParentCandidates(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.GetMyParentCandidatesAsync(workId, actorUserId, ct);
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("work-assignments/{id}")]
    public async Task<ActionResult<WorkAssignmentResponse>> GetById(
        [FromRoute] string id,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.GetByIdAsync(id, actorUserId, ct);
            if (rs is null) return NotFound();
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("work-assignments/{parentAssignmentId}/children")]
    public async Task<ActionResult<List<WorkAssignmentListResponse>>> GetChildren(
        [FromRoute] string parentAssignmentId,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.GetChildrenAsync(parentAssignmentId, actorUserId, ct);
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("works/{workId}/assignments")]
    public async Task<ActionResult<WorkAssignmentResponse>> Create(
        [FromRoute] string workId,
        [FromBody] SaveWorkAssignmentRequest req,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.CreateAsync(workId, req, actorUserId, ct);
            return CreatedAtAction(nameof(GetById), new { id = rs.Id }, rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("work-assignments/{id}/deactivate")]
    public async Task<ActionResult> Deactivate(
        [FromRoute] string id,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var ok = await _service.DeactivateAsync(id, actorUserId, ct);
            if (!ok) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("work-assignments/{id}/activate")]
    public async Task<ActionResult> Activate(
        [FromRoute] string id,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var ok = await _service.ActivateAsync(id, actorUserId, ct);
            if (!ok) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return ForbidWithMessage(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private string GetActorUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirstValue("sub")
               ?? throw new UnauthorizedAccessException("Không xác định được người dùng.");
    }

    private ActionResult ForbidWithMessage(string message)
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { message });
    }
}