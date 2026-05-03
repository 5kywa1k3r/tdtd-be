using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.WorkAssignments.Aggregate;
using tdtd_be.Services.WorkAssignments.Aggregate;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/work-assignment-aggregate")]
public sealed class WorkAssignmentAggregateController : ControllerBase
{
    private readonly IWorkAssignmentAggregateService _service;

    public WorkAssignmentAggregateController(IWorkAssignmentAggregateService service)
    {
        _service = service;
    }

    [HttpPost("view")]
    public async Task<IActionResult> GetView(
        [FromBody] AggregateReportRequest req,
        CancellationToken ct)
    {
        var result = await _service.GetAggregatedViewAsync(req, ct);
        return Ok(result);
    }
}