using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Nq57;
using tdtd_be.Services.Nq57;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/nq57")]
[Authorize]
public sealed class Nq57IntegrationController : ControllerBase
{
    private readonly INq57IntegrationService _service;

    public Nq57IntegrationController(INq57IntegrationService service)
    {
        _service = service;
    }

    [HttpPost("build-save-payload")]
    public ActionResult<Nq57BuildSavePayloadResponse> BuildSavePayload(
        [FromBody] Nq57BuildSavePayloadRequest request)
        => _service.BuildSavePayload(request);

    [HttpPost("push")]
    public Task<Nq57PushResponse> Push(
        [FromBody] Nq57PushRequest request,
        CancellationToken ct)
        => _service.PushAsync(request, ct);
}
