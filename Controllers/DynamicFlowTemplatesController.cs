using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Controllers;

// Bản deploy gọn: tạm ngừng thiết kế Dynamic Flow, giữ mã để mở lại.
[NonController]
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
    [ProducesResponseType(typeof(PagedResult<DynamicFlowTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<DynamicFlowTemplateDto>>> Search(
        [FromBody] DynamicFlowTemplateSearchRequest req,
        CancellationToken ct)
    {
        var result = await _service.SearchAsync(
            req ?? new DynamicFlowTemplateSearchRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpGet("{familyId}")]
    [ProducesResponseType(typeof(DynamicFlowTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DynamicFlowTemplateDto>> Get(
        [FromRoute] string familyId,
        CancellationToken ct)
    {
        var result = await _service.GetAsync(familyId, GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(DynamicFlowTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateDto>> Create(
        [FromBody] CreateDynamicFlowTemplateRequest req,
        CancellationToken ct,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        req ??= new CreateDynamicFlowTemplateRequest();
        ApplyCreateCommandId(req, idempotencyKey);
        var result = await _service.CreateAsync(req, GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpPut("{familyId}")]
    [ProducesResponseType(typeof(DynamicFlowTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateDto>> Update(
        [FromRoute] string familyId,
        [FromBody] UpdateDynamicFlowTemplateRequest req,
        CancellationToken ct)
    {
        var result = await _service.UpdateAsync(familyId, req ?? new UpdateDynamicFlowTemplateRequest(), GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpDelete("{familyId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(
        [FromRoute] string familyId,
        [FromBody] DeleteDynamicFlowTemplateRequest req,
        CancellationToken ct)
    {
        await _service.DeleteAsync(
            familyId,
            req ?? new DeleteDynamicFlowTemplateRequest(),
            GetActorUserId(),
            ct);
        return NoContent();
    }

    [HttpPost("{familyId}/archive")]
    [ProducesResponseType(typeof(DynamicFlowTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateDto>> Archive(
        [FromRoute] string familyId,
        [FromBody] ArchiveDynamicFlowTemplateRequest req,
        CancellationToken ct)
    {
        var result = await _service.ArchiveAsync(
            familyId,
            req ?? new ArchiveDynamicFlowTemplateRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("{familyId}/clone")]
    [ProducesResponseType(typeof(DynamicFlowTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateDto>> Clone(
        [FromRoute] string familyId,
        [FromBody] CloneDynamicFlowTemplateRequest req,
        CancellationToken ct)
    {
        var result = await _service.CloneAsync(
            familyId,
            req ?? new CloneDynamicFlowTemplateRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpGet("{familyId}/versions")]
    [ProducesResponseType(typeof(List<DynamicFlowTemplateVersionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<DynamicFlowTemplateVersionDto>>> ListVersions(
        [FromRoute] string familyId,
        CancellationToken ct)
    {
        var result = await _service.ListVersionsAsync(familyId, GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpGet("{familyId}/versions/{versionId}")]
    [ProducesResponseType(typeof(DynamicFlowTemplateVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DynamicFlowTemplateVersionDto>> GetVersion(
        [FromRoute] string familyId,
        [FromRoute] string versionId,
        CancellationToken ct)
    {
        var result = await _service.GetVersionAsync(familyId, versionId, GetActorUserId(), ct);
        return Ok(result);
    }

    [HttpPut("{familyId}/versions/{versionId}/draft")]
    [ProducesResponseType(typeof(DynamicFlowTemplateVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateVersionDto>> SaveDraftVersion(
        [FromRoute] string familyId,
        [FromRoute] string versionId,
        [FromBody] SaveDynamicFlowTemplateVersionDraftRequest req,
        CancellationToken ct)
    {
        var result = await _service.SaveDraftVersionAsync(
            familyId,
            versionId,
            req ?? new SaveDynamicFlowTemplateVersionDraftRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPut("{familyId}/versions/draft")]
    [Obsolete("Use PUT /{familyId}/versions/{versionId}/draft.")]
    [ProducesResponseType(typeof(DynamicFlowTemplateVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateVersionDto>> SaveDraftVersionLegacy(
        [FromRoute] string familyId,
        [FromBody] SaveDynamicFlowTemplateVersionDraftRequest req,
        CancellationToken ct)
    {
        var result = await _service.SaveDraftVersionAsync(
            familyId,
            req ?? new SaveDynamicFlowTemplateVersionDraftRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("{familyId}/versions/{versionId}/lock")]
    [ProducesResponseType(typeof(DynamicFlowTemplateVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateVersionDto>> LockVersion(
        [FromRoute] string familyId,
        [FromRoute] string versionId,
        [FromBody] LockDynamicFlowTemplateVersionRequest req,
        CancellationToken ct)
    {
        var result = await _service.LockVersionAsync(
            familyId,
            versionId,
            req ?? new LockDynamicFlowTemplateVersionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("versions/{versionId}/lock")]
    [Obsolete("Use POST /{familyId}/versions/{versionId}/lock.")]
    [ProducesResponseType(typeof(DynamicFlowTemplateVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateVersionDto>> LockVersionLegacy(
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

    [HttpPost("{familyId}/versions/{versionId}/reopen")]
    [ProducesResponseType(typeof(DynamicFlowTemplateVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowTemplateVersionDto>> ReopenVersion(
        [FromRoute] string familyId,
        [FromRoute] string versionId,
        [FromBody] ReopenDynamicFlowTemplateVersionRequest req,
        CancellationToken ct)
    {
        var result = await _service.ReopenVersionAsync(
            familyId,
            versionId,
            req ?? new ReopenDynamicFlowTemplateVersionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("{familyId}/versions/diff")]
    [ProducesResponseType(typeof(DiffDynamicFlowTemplateVersionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiffDynamicFlowTemplateVersionsDto>> DiffVersions(
        [FromRoute] string familyId,
        [FromBody] DiffDynamicFlowTemplateVersionsRequest req,
        CancellationToken ct)
    {
        var result = await _service.DiffVersionsAsync(
            familyId,
            req ?? new DiffDynamicFlowTemplateVersionsRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    private static void ApplyCreateCommandId(
        CreateDynamicFlowTemplateRequest req,
        string? idempotencyKey)
    {
        var bodyCommandId = req.CommandId?.Trim() ?? string.Empty;
        var headerCommandId = idempotencyKey?.Trim() ?? string.Empty;

        if (bodyCommandId.Length > 0 &&
            headerCommandId.Length > 0 &&
            !string.Equals(bodyCommandId, headerCommandId, StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                new
                {
                    reason = "DYNAMIC_FLOW_IDEMPOTENCY_KEY_MISMATCH",
                    path = "commandId"
                });
        }

        req.CommandId = bodyCommandId.Length > 0 ? bodyCommandId : headerCommandId;
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
