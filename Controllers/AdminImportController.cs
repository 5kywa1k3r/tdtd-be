using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.AdminImport;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize]
public sealed class AdminImportController : ControllerBase
{
    private readonly IAdminImportService _svc;

    public AdminImportController(IAdminImportService svc)
    {
        _svc = svc;
    }

    [HttpGet("units/import-template")]
    public async Task<IActionResult> UnitTemplate([FromQuery] string? format, CancellationToken ct)
    {
        var file = await _svc.BuildUnitTemplateAsync(format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("units/import")]
    [Consumes("multipart/form-data")]
    public Task<ImportResult> ImportUnits([FromForm] ImportFileForm form, [FromQuery] bool dryRun = true, CancellationToken ct = default)
        => _svc.ImportUnitsAsync(form.File, dryRun, ct);

    [HttpGet("users/import-template")]
    public async Task<IActionResult> UserTemplate([FromQuery] string? format, CancellationToken ct)
    {
        var file = await _svc.BuildUserTemplateAsync(format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("users/import")]
    [Consumes("multipart/form-data")]
    public Task<ImportResult> ImportUsers([FromForm] ImportFileForm form, [FromQuery] bool dryRun = true, CancellationToken ct = default)
        => _svc.ImportUsersAsync(form.File, dryRun, ct);
}

public sealed class ImportFileForm
{
    public IFormFile File { get; set; } = default!;
}
