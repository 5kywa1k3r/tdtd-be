using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class DynamicFormCloneRequestsController : ControllerBase
{
    private readonly IDynamicFormCloneRequestService _service;

    public DynamicFormCloneRequestsController(IDynamicFormCloneRequestService service)
    {
        _service = service;
    }

    [HttpPost("work-assignments/{assignmentId}/dynamic-form-clone-requests")]
    public Task<DynamicFormCloneRequestRow> Create(
        [FromRoute] string assignmentId,
        [FromBody] CreateDynamicFormCloneRequestReq req,
        CancellationToken ct)
        => _service.CreateAsync(assignmentId, req, GetActorUserId(), ct);

    [HttpPost("works/{workId}/dynamic-form-clone-requests/my")]
    public Task<PagedResult<DynamicFormCloneRequestRow>> SearchMy(
        [FromRoute] string workId,
        [FromBody] DynamicFormCloneRequestSearchReq req,
        CancellationToken ct)
        => _service.SearchMyAsync(workId, req, GetActorUserId(), ct);

    [HttpPost("works/{workId}/dynamic-form-clone-requests/pending-approval")]
    public Task<PagedResult<DynamicFormCloneRequestRow>> SearchPendingApproval(
        [FromRoute] string workId,
        [FromBody] DynamicFormCloneRequestSearchReq req,
        CancellationToken ct)
        => _service.SearchPendingApprovalAsync(workId, req, GetActorUserId(), ct);

    [HttpPost("dynamic-form-clone-requests/{id}/approve")]
    public Task<DynamicFormCloneRequestRow> Approve(
        [FromRoute] string id,
        [FromBody] ReviewDynamicFormCloneRequestReq req,
        CancellationToken ct)
        => _service.ApproveAsync(id, req, GetActorUserId(), ct);

    [HttpPost("dynamic-form-clone-requests/{id}/reject")]
    public Task<DynamicFormCloneRequestRow> Reject(
        [FromRoute] string id,
        [FromBody] ReviewDynamicFormCloneRequestReq req,
        CancellationToken ct)
        => _service.RejectAsync(id, req, GetActorUserId(), ct);

    private string GetActorUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.FindFirstValue("sub")
           ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.AUTH_ME_NOT_AVAILABLE);
}
