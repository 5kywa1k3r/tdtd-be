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
    public async Task<ActionResult<List<WorkAssignmentResponse>>> GetByWorkId(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        try
        {
            var rs = await _service.GetByWorkIdAsync(workId, ct);
            return Ok(rs);
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
            var rs = await _service.GetByIdAsync(id, ct);
            if (rs is null) return NotFound();
            return Ok(rs);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("works/{workId}/assignments/by-dynamic-excel/{dynamicExcelId}")]
    public async Task<ActionResult<List<WorkAssignmentResponse>>> GetByDynamicExcel(
        [FromRoute] string workId,
        [FromRoute] string dynamicExcelId,
        CancellationToken ct)
    {
        try
        {
            var rs = await _service.GetByDynamicExcelAsync(workId, dynamicExcelId, ct);
            return Ok(rs);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("work-assignments/{parentAssignmentId}/children")]
    public async Task<ActionResult<List<WorkAssignmentResponse>>> GetChildren(
        [FromRoute] string parentAssignmentId,
        CancellationToken ct)
    {
        try
        {
            var rs = await _service.GetChildrenAsync(parentAssignmentId, ct);
            return Ok(rs);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("work-assignments/{parentAssignmentId}/children/by-dynamic-excel/{dynamicExcelId}")]
    public async Task<ActionResult<List<WorkAssignmentResponse>>> GetChildrenByDynamicExcel(
        [FromRoute] string parentAssignmentId,
        [FromRoute] string dynamicExcelId,
        CancellationToken ct)
    {
        try
        {
            var rs = await _service.GetChildrenByDynamicExcelAsync(parentAssignmentId, dynamicExcelId, ct);
            return Ok(rs);
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
            return Unauthorized(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("work-assignments/{id}")]
    public async Task<ActionResult<WorkAssignmentResponse>> Update(
        [FromRoute] string id,
        [FromBody] SaveWorkAssignmentRequest req,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var rs = await _service.UpdateAsync(id, req, actorUserId, ct);
            return Ok(rs);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("work-assignments/{id}")]
    public async Task<ActionResult> SoftDelete(
        [FromRoute] string id,
        CancellationToken ct)
    {
        try
        {
            var actorUserId = GetActorUserId();
            var ok = await _service.SoftDeleteAsync(id, actorUserId, ct);
            if (!ok) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
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
}