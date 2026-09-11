using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/labels")]
[Authorize]
public sealed class LabelsController : ControllerBase
{
    private readonly ILabelService _svc;
    private readonly ILabelConfigCommandService _config;

    public LabelsController(ILabelService svc, ILabelConfigCommandService config)
    {
        _svc = svc;
        _config = config;
    }

    [HttpPost("search")]
    public Task<PagedResult<LabelRow>> Search([FromBody] LabelSearchReq req, CancellationToken ct)
        => _svc.SearchAsync(req, ct);

    [HttpGet("{id}")]
    public Task<LabelRow> GetById([FromRoute] string id, CancellationToken ct)
        => _svc.GetByIdAsync(id, ct);

    [HttpPost]
    public Task<LabelConfigResult> Create(
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _config.CreateAsync(
            StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelConfigPayload>>(body),
            ct);

    [HttpPut("{id}")]
    public Task<LabelConfigResult> Update(
        [FromRoute] string id,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _config.UpdateAsync(
            id,
            StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelConfigPayload>>(body),
            ct);

    [HttpDelete("{id}")]
    public Task<LabelConfigResult> Delete(
        [FromRoute] string id,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _config.TombstoneAsync(
            id,
            StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelTombstonePayload>>(body),
            ct);

    [HttpGet("{id}/config")]
    public Task<LabelConfigResult> GetConfig([FromRoute] string id, CancellationToken ct)
        => _config.GetAsync(id, ct);

    [HttpPost("config")]
    public Task<LabelConfigResult> CreateConfig(
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _config.CreateAsync(
            StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelConfigPayload>>(body),
            ct);

    [HttpPut("{id}/config")]
    public Task<LabelConfigResult> UpdateConfig(
        [FromRoute] string id,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _config.UpdateAsync(
            id,
            StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelConfigPayload>>(body),
            ct);

    [HttpPost("{id}/config/tombstone")]
    public Task<LabelConfigResult> TombstoneConfig(
        [FromRoute] string id,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => _config.TombstoneAsync(
            id,
            StatConfigCanonicalJson.DeserializeStrict<StatConfigMutationEnvelope<LabelTombstonePayload>>(body),
            ct);
}
