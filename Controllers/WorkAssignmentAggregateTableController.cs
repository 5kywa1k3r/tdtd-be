using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.Services.WorkAssignments.Aggregate;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/work-assignment-aggregate-table")]
public sealed class WorkAssignmentAggregateTableController : ControllerBase
{
    private readonly IAggregateTableService _service;

    public WorkAssignmentAggregateTableController(IAggregateTableService service)
    {
        _service = service;
    }

    [HttpPost("table")]
    public async Task<IActionResult> Table(
        [FromBody] AggregateTableRequest req,
        CancellationToken ct)
    {
        try
        {
            var result = await _service.GetTableAsync(req, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("dynamic-form/table")]
    public async Task<IActionResult> DynamicFormTable(
        [FromBody] DynamicFormAggregateRequest req,
        CancellationToken ct)
    {
        try
        {
            var result = await _service.GetDynamicFormAggregateAsync(req, ct);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
