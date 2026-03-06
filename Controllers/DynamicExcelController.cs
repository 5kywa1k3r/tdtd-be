using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/dynamic-excel")]
[Authorize]
public sealed class DynamicExcelController : ControllerBase
{
    private readonly IDynamicExcelService _svc;

    public DynamicExcelController(IDynamicExcelService svc)
    {
        _svc = svc;
    }

    [HttpPost("search")]
    public Task<PagedResult<DynamicExcelRow>> Search([FromBody] DynamicExcelSearchReq req, CancellationToken ct)
        => _svc.SearchAsync(req, ct);

    [HttpGet("next-code")]
    public Task<NextCodeResp> NextCode([FromQuery] int? year, CancellationToken ct)
        => _svc.GetNextCodeAsync(year, ct);

    [HttpGet("{id}")]
    public Task<DynamicExcelDetail> GetById([FromRoute] string id, CancellationToken ct)
        => _svc.GetByIdAsync(id, ct);

    [HttpPost]
    public Task<DynamicExcelDetail> Create([FromBody] CreateDynamicExcelReq req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPut("{id}")]
    public Task<DynamicExcelDetail> Update([FromRoute] string id, [FromBody] UpdateDynamicExcelReq req, CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpDelete("{id}")]
    public Task Delete([FromRoute] string id, CancellationToken ct)
        => _svc.DeleteAsync(id, ct);
}