using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api")]
public sealed class WorkAssignmentReportsController : ControllerBase
{
    private readonly IWorkAssignmentReportService _service;

    public WorkAssignmentReportsController(IWorkAssignmentReportService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lấy user id hiện tại từ claims.
    /// Sửa lại nếu project của bệ hạ đang dùng claim type khác.
    /// </summary>
    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? User.FindFirstValue("userId")
            ?? throw new UnauthorizedAccessException("Không xác định được người dùng hiện tại.");
    }

    /// <summary>
    /// Lấy danh sách report của một WorkAssignment.
    /// </summary>
    [HttpGet("work-assignments/{workAssignmentId}/reports")]
    public async Task<IActionResult> GetByAssignment(
        [FromRoute] string workAssignmentId,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var rs = await _service.GetByAssignmentAsync(workAssignmentId, currentUserId, ct);
        return Ok(rs);
    }

    /// <summary>
    /// Search report có phân trang.
    /// </summary>
    [HttpPost("work-assignment-reports/search")]
    public async Task<IActionResult> Search(
        [FromBody] WorkAssignmentReportSearchRequest req,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var rs = await _service.SearchAsync(req, currentUserId, ct);
        return Ok(rs);
    }

    /// <summary>
    /// Lấy chi tiết một report.
    /// </summary>
    [HttpGet("work-assignment-reports/{id}")]
    public async Task<IActionResult> GetById(
        [FromRoute] string id,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var rs = await _service.GetByIdAsync(id, currentUserId, ct);
        return Ok(rs);
    }

    /// <summary>
    /// Khởi tạo draft report mới cho một kỳ của assignment.
    /// </summary>
    [HttpPost("work-assignments/{workAssignmentId}/reports/init")]
    public async Task<IActionResult> InitDraft(
        [FromRoute] string workAssignmentId,
        [FromBody] InitWorkAssignmentReportRequest req,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var rs = await _service.InitDraftAsync(workAssignmentId, req, currentUserId, ct);
        return Ok(rs);
    }

    /// <summary>
    /// Lưu draft report.
    /// FE gửi workbook + values1D đã flatten.
    /// </summary>
    [HttpPut("work-assignment-reports/{id}/draft")]
    public async Task<IActionResult> SaveDraft(
        [FromRoute] string id,
        [FromBody] SaveWorkAssignmentReportDraftRequest req,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var rs = await _service.SaveDraftAsync(id, req, currentUserId, ct);
        return Ok(rs);
    }

    /// <summary>
    /// Danh sách ngoài cùng của user trong 1 Work, nhóm theo template.
    /// </summary>
    [HttpPost("works/{workId}/my-report-templates/search")]
    public async Task<IActionResult> SearchMyReportTemplates(
        [FromRoute] string workId,
        [FromBody] MyReportTemplateSearchRequest req,
        CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        var rs = await _service.SearchMyReportTemplatesAsync(workId, req, currentUserId, ct);
        return Ok(rs);
    }
}