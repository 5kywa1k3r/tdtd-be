using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.EvaluationTemplates;
using tdtd_be.Services.EvaluationTemplates;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/evaluation-templates")]
[Authorize]
public sealed class EvaluationTemplatesController : ControllerBase
{
    private readonly IEvaluationTemplateService _service;

    public EvaluationTemplatesController(IEvaluationTemplateService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EvaluationTemplateDto>>> GetAll([FromQuery] bool includeInactive = false, CancellationToken ct = default)
        => Ok(includeInactive ? await _service.GetAllAsync(ct) : await _service.GetActiveAsync(ct));

    [HttpGet("{id}")]
    public async Task<ActionResult<EvaluationTemplateDto>> GetById([FromRoute] string id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<EvaluationTemplateDto>> Create([FromBody] CreateEvaluationTemplateRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    [HttpPut("{id}")]
    public async Task<ActionResult<EvaluationTemplateDto>> Update([FromRoute] string id, [FromBody] UpdateEvaluationTemplateRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    [HttpPost("{id}/deactivate")]
    public async Task<ActionResult> Deactivate([FromRoute] string id, CancellationToken ct)
    {
        await _service.DeactivateAsync(id, ct);
        return Ok();
    }
}
