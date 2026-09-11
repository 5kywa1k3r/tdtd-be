using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/stat-runs/exports")]
public sealed class StatRunExportsController : ControllerBase
{
    private readonly IStatRunExportService _service;
    private readonly MeAccessor _me;

    public StatRunExportsController(IStatRunExportService service, MeAccessor me)
    {
        _service = service;
        _me = me;
    }

    [HttpPost]
    public async Task<ActionResult<StatRunExportResponse>> Create(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunExportCreateRequest>(body);
        var result = await _service.CreateAsync(request, _me.RequireMe(), ct);
        return StatusCode(
            result.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            result);
    }

    [HttpGet("{exportId}")]
    public Task<StatRunExportResponse> Get(
        string exportId,
        [FromQuery] string workId,
        [FromQuery] string scopeType,
        [FromQuery] string scopeId,
        [FromQuery] string capabilityId,
        CancellationToken ct)
        => _service.GetAsync(
            exportId,
            workId,
            scopeType,
            scopeId,
            capabilityId,
            _me.RequireMe(),
            ct);

    [HttpGet("{exportId}/download")]
    public async Task<IActionResult> Download(
        string exportId,
        [FromQuery] string workId,
        [FromQuery] string scopeType,
        [FromQuery] string scopeId,
        [FromQuery] string capabilityId,
        CancellationToken ct)
    {
        var result = await _service.DownloadAsync(
            exportId,
            workId,
            scopeType,
            scopeId,
            capabilityId,
            _me.RequireMe(),
            ct);
        Response.Headers.ETag = $"\"sha256-{result.ContentHash}\"";
        Response.Headers["X-Content-SHA256"] = result.ContentHash;
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(result.Content, result.ContentType, result.FileName);
    }

    [HttpPost("~/api/operations/stat-runs/exports/cleanup")]
    public Task<StatRunExportCleanupResponse> Cleanup(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunExportCleanupRequest>(body);
        return _service.CleanupExpiredAsync(request, _me.RequireMe(), ct);
    }
}
