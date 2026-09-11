using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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

    [HttpGet("{id}/versions")]
    public Task<DynamicFormVersionHistoryResp> GetVersionHistory(
        [FromRoute] string id,
        CancellationToken ct)
        => _svc.GetVersionHistoryAsync(id, ct);

    [HttpPost]
    public Task<DynamicFormDetail> Create([FromBody] CreateDynamicFormReq req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPost("{id}/versions")]
    public Task<DynamicFormDetail> CreateNextVersion(
        [FromRoute] string id,
        [FromBody] CreateDynamicFormVersionReq req,
        CancellationToken ct)
        => _svc.CreateNextVersionAsync(id, req, ct);

    [HttpPut("{id}")]
    public Task<DynamicFormDetail> Update(
        [FromRoute] string id,
        [FromBody] UpdateDynamicFormReq req,
        CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpGet("{id}/statistics")]
    public Task<DynamicFormStatisticConfigResult> GetStatistics(
        [FromRoute] string id,
        CancellationToken ct)
        => _svc.GetStatisticsAsync(id, ct);

    [HttpPatch("{id}/statistics")]
    public Task<DynamicFormStatisticConfigResult> UpdateStatisticConfig(
        [FromRoute] string id,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _svc.UpdateStatisticConfigAsync(id, body, ct);

    [HttpPost("{id}/publish")]
    public Task<DynamicFormDetail> Publish(
        [FromRoute] string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PublishDynamicFormReq? req,
        CancellationToken ct)
        => _svc.PublishAsync(id, req, ct);

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

    [HttpPost("{id}/blocks/import-dynamic-excel")]
    public Task<DynamicFormDetail> ImportDynamicExcelBlock(
        [FromRoute] string id,
        [FromBody] ImportDynamicExcelBlockReq req,
        CancellationToken ct)
        => _svc.ImportDynamicExcelBlockAsync(id, req, ct);

    [HttpDelete("{id}")]
    public Task Delete(
        [FromRoute] string id,
        [FromQuery] int? expectedRevision,
        CancellationToken ct)
        => _svc.DeleteAsync(id, expectedRevision, ct);
}
