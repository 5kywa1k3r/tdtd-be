using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;

namespace tdtd_be.Uploads;

public sealed class CreateUploadSessionReq
{
    public string FileName { get; set; } = default!;
    public long Size { get; set; }
    public string? Mime { get; set; }
    public string SourceType { get; set; } = "UPLOAD";
    public string? SourceId { get; set; }
}

[ApiController]
[Route("api/upload-sessions")]
public sealed class UploadSessionsController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly UploadTokenService _uploadTokenSvc;
    private readonly MeAccessor _me;

    public UploadSessionsController(IConfiguration cfg, UploadTokenService uploadTokenSvc, MeAccessor me)
    {
        _cfg = cfg;
        _uploadTokenSvc = uploadTokenSvc;
        _me = me;
    }

    [Authorize]
    [HttpPost]
    public IActionResult Create([FromBody] CreateUploadSessionReq req)
    {
        if (string.IsNullOrWhiteSpace(req.FileName)) return BadRequest("FileName is required.");
        if (req.Size <= 0) return BadRequest("Size must be > 0.");

        var maxBytes = long.Parse(_cfg["Tus:MaxUploadBytes"] ?? "524288000");
        if (req.Size > maxBytes) return BadRequest($"File too large. Max={maxBytes} bytes.");

        var ttl = int.Parse(_cfg["Tus:UploadTokenTtlSeconds"] ?? "900"); // 15 phút default
        var chunkSize = long.Parse(_cfg["Tus:ChunkSizeBytes"] ?? (512 * 1024).ToString());

        var me = _me.RequireMe();

        // ✅ issue upload token (đúng logic bệ hạ đã có)
        var uploadToken = _uploadTokenSvc.Issue(
            userId: me.Id,
            fileName: req.FileName,
            mime: req.Mime ?? "application/octet-stream",
            size: req.Size,
            sourceType: string.IsNullOrWhiteSpace(req.SourceType) ? "UPLOAD" : req.SourceType,
            sourceId: req.SourceId,
            ttlSeconds: ttl
        );

        var apiBase = $"{Request.Scheme}://{Request.Host}";
        var endpoint = $"{apiBase}/api/uploads";

        // ✅ trả contract chuẩn cho FE
        return Ok(new
        {
            endpoint,
            uploadToken,
            chunkSize,
            maxSize = maxBytes
        });
    }
}