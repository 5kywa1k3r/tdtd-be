using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/dynamic-forms")]
[Authorize]
public sealed class DynamicFormController : ControllerBase
{
    private readonly IDynamicFormService _svc;

    public DynamicFormController(IDynamicFormService svc)
    {
        _svc = svc;
    }

    [HttpPost("search")]
    public Task<PagedResult<DynamicFormRow>> Search([FromBody] DynamicFormSearchReq req, CancellationToken ct)
        => _svc.SearchAsync(req, ct);

    [HttpGet("next-code")]
    public Task<NextCodeResp> NextCode([FromQuery] int? year, CancellationToken ct)
        => _svc.GetNextCodeAsync(year, ct);

    [HttpGet("{id}")]
    public Task<DynamicFormDetail> GetById([FromRoute] string id, CancellationToken ct)
        => _svc.GetByIdAsync(id, ct);

    [HttpPost]
    public Task<DynamicFormDetail> Create([FromBody] CreateDynamicFormReq req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPut("{id}")]
    public Task<DynamicFormDetail> Update(
        [FromRoute] string id,
        [FromBody] UpdateDynamicFormReq req,
        CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpPost("{id}/publish")]
    public Task<DynamicFormDetail> Publish([FromRoute] string id, CancellationToken ct)
        => _svc.PublishAsync(id, ct);

    [HttpPost("{id}/clone")]
    public Task<DynamicFormDetail> Clone(
        [FromRoute] string id,
        [FromBody] CloneDynamicFormReq req,
        CancellationToken ct)
        => _svc.CloneAsync(id, req, ct);

    [HttpPost("wrap-dynamic-excel")]
    public Task<DynamicFormDetail> WrapDynamicExcel(
        [FromBody] WrapDynamicExcelAsFormReq req,
        CancellationToken ct)
        => _svc.WrapDynamicExcelAsync(req, ct);

    [HttpDelete("{id}")]
    public Task Delete([FromRoute] string id, CancellationToken ct)
        => _svc.DeleteAsync(id, ct);
}
