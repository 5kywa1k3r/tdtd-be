using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Works;
using tdtd_be.Models;
using tdtd_be.Services.Works;
using static tdtd_be.Services.Works.WorkServices;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/works")]
[Authorize]
public sealed class WorksController : ControllerBase
{
    private readonly IWorkService _svc;

    public WorksController(IWorkService svc)
    {
        _svc = svc;
    }

    [HttpPost]
    public async Task<ActionResult<WorkResponse>> Create([FromBody] WorkCreateRequest req, CancellationToken ct)
        => Ok(await _svc.CreateAsync(req, ct));

    [HttpGet("{id}")]
    public async Task<ActionResult<WorkResponse>> GetById([FromRoute] string id, CancellationToken ct)
        => Ok(await _svc.GetByIdAsync(id, ct));

    [HttpGet]
    public async Task<ActionResult<PagedResult<WorkListRow>>> Search(
        [FromQuery] string? q,
        [FromQuery] int? status,
        [FromQuery] WorkType type,
        [FromQuery] WorkPriority? priority,
        [FromQuery] string? leaderDirectiveUserId,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? sortField = "createdAtUtc",
        [FromQuery] string? sortDirection = "desc",
        CancellationToken ct = default)
    {
        var req = new WorkSearchRequest(
            Q: q,
            Status: status is null ? null : (Models.WorkStatus?)status.Value,
            Type: type,
            Priority: priority,
            LeaderDirectiveUserId: leaderDirectiveUserId,
            Page: page,
            PageSize: pageSize,
            SortField: sortField,
            SortDirection: sortDirection
        );

        return Ok(await _svc.SearchAsync(req, ct));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<WorkResponse>> Update([FromRoute] string id, [FromBody] WorkUpdateRequest req, CancellationToken ct)
        => Ok(await _svc.UpdateAsync(id, req, ct));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }
}