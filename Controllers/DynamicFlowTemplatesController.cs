using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/dynamic-flow-templates")]
public sealed class DynamicFlowTemplatesController : ControllerBase
{
    private readonly IDynamicFlowTemplateService _service;

    public DynamicFlowTemplatesController(IDynamicFlowTemplateService service)
    {
        _service = service;
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search(
        [FromBody] DynamicFlowTemplateSearchRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchAsync(req ?? new DynamicFlowTemplateSearchRequest(), ct);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(
        [FromRoute] string id,
        CancellationToken ct)
    {
        var result = await _service.GetAsync(id, ct);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateDynamicFlowTemplateRequest req,
        CancellationToken ct)
    {
        var result = await _service.CreateAsync(req ?? new CreateDynamicFlowTemplateRequest(), GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        [FromRoute] string id,
        [FromBody] UpdateDynamicFlowTemplateRequest req,
        CancellationToken ct)
    {
        var result = await _service.UpdateAsync(id, req ?? new UpdateDynamicFlowTemplateRequest(), GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        [FromRoute] string id,
        CancellationToken ct)
    {
        await _service.DeleteAsync(id, GetActorUserId(), ct);
        return NoContent();
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> ListVersions(
        [FromRoute] string id,
        CancellationToken ct)
    {
        var result = await _service.ListVersionsAsync(id, ct);
        return Ok(result);
    }

    [HttpPut("{id}/versions/draft")]
    public async Task<IActionResult> SaveDraftVersion(
        [FromRoute] string id,
        [FromBody] SaveDynamicFlowTemplateVersionDraftRequest req,
        CancellationToken ct)
    {
        var result = await _service.SaveDraftVersionAsync(
            id,
            req ?? new SaveDynamicFlowTemplateVersionDraftRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("versions/{versionId}/lock")]
    public async Task<IActionResult> LockVersion(
        [FromRoute] string versionId,
        [FromBody] LockDynamicFlowTemplateVersionRequest req,
        CancellationToken ct)
    {
        var result = await _service.LockVersionAsync(
            versionId,
            req ?? new LockDynamicFlowTemplateVersionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
