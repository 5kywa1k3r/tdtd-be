using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.UnitTypes;
using tdtd_be.Services;
using static tdtd_be.Services.UnitService;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/admin/unit-types")]
[Authorize]
public sealed class UnitTypesController : ControllerBase
{
    private readonly IUnitTypeAdminService _svc;
    public UnitTypesController(IUnitTypeAdminService svc) => _svc = svc;

    [HttpPost] public Task<UnitTypeResponse> Create([FromBody] CreateUnitTypeRequest req, CancellationToken ct) => _svc.CreateAsync(req, ct);
    [HttpPut("{id}")] public Task<UnitTypeResponse> Update(string id, [FromBody] UpdateUnitTypeRequest req, CancellationToken ct) => _svc.UpdateAsync(id, req, ct);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct) { await _svc.DeleteAsync(id, ct); return NoContent(); }

    [HttpGet] public Task<IReadOnlyList<UnitTypeResponse>> List([FromQuery] bool? isDeleted, CancellationToken ct) => _svc.ListAsync(isDeleted, ct);
}