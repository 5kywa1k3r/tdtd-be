using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class WorkAssignmentReportsController : ControllerBase
{
    private readonly IWorkAssignmentReportService _service;

    public WorkAssignmentReportsController(IWorkAssignmentReportService service)
    {
        _service = service;
    }

    [HttpPost("works/{workId}/my-report-templates/search")]
    public async Task<IActionResult> SearchMyReportTemplates([FromRoute] string workId, [FromBody] MyReportTemplateSearchRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.SearchMyReportTemplatesAsync(workId, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("works/{workId}/my-report-templates/{dynamicFormTemplateId}")]
    public async Task<IActionResult> GetMyReportTemplateDetail(
        [FromRoute] string workId,
        [FromRoute] string dynamicFormTemplateId,
        [FromQuery] string? scopeAssignmentId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetMyReportTemplateDetailAsync(workId, dynamicFormTemplateId, actorUserId, scopeAssignmentId, ct);
        return Ok(rs);
    }

    [HttpPost("work-report-periods/{workReportPeriodId}/open")]
    public async Task<IActionResult> OpenPeriod([FromRoute] string workReportPeriodId, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.OpenPeriodAsync(workReportPeriodId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignments/{workAssignmentId}/reports/init")]
    public async Task<IActionResult> InitDraft([FromRoute] string workAssignmentId, [FromBody] InitWorkAssignmentReportRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.InitDraftAsync(workAssignmentId, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignments/{workAssignmentId}/reports")]
    public async Task<IActionResult> GetByAssignment([FromRoute] string workAssignmentId, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetByAssignmentAsync(workAssignmentId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/search")]
    public async Task<IActionResult> Search([FromBody] WorkAssignmentReportSearchRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.SearchAsync(req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignment-reports/{id}")]
    public async Task<IActionResult> GetById([FromRoute] string id, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetByIdAsync(id, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignment-reports/{id}/sections")]
    public async Task<IActionResult> GetSections([FromRoute] string id, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetSectionSummariesAsync(id, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignment-reports/{id}/sections/{sectionId}")]
    public async Task<IActionResult> GetSectionDetail(
        [FromRoute] string id,
        [FromRoute] string sectionId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetSectionDetailAsync(id, sectionId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignment-reports/{id}/template-workbook/{dynamicExcelTemplateId}")]
    public async Task<IActionResult> GetTemplateWorkbook(
        [FromRoute] string id,
        [FromRoute] string dynamicExcelTemplateId,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetReportTemplateWorkbookAsync(id, dynamicExcelTemplateId, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPut("work-assignment-reports/{id}/draft")]
    public async Task<IActionResult> SaveDraft([FromRoute] string id, [FromBody] SaveWorkAssignmentReportDraftRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.SaveDraftAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPatch("work-assignment-reports/{id}/draft/patch")]
    public async Task<IActionResult> SaveDraftPatch([FromRoute] string id, [FromBody] SaveWorkAssignmentReportDraftPatchRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.SaveDraftPatchAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/draft/apply-dynamic-form-aggregate")]
    public async Task<IActionResult> ApplyDynamicFormAggregateDraft([FromRoute] string id, [FromBody] ApplyDynamicFormAggregateDraftRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.ApplyDynamicFormAggregateDraftAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/draft/preview-dynamic-form-aggregate")]
    public async Task<IActionResult> PreviewDynamicFormAggregateDraft([FromRoute] string id, [FromBody] ApplyDynamicFormAggregateDraftRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.PreviewDynamicFormAggregateDraftAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/submit")]
    public async Task<IActionResult> Submit([FromRoute] string id, [FromBody] SubmitWorkAssignmentReportRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.SubmitAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/withdraw-submitted")]
    public async Task<IActionResult> WithdrawSubmitted([FromRoute] string id, [FromBody] ReturnWorkAssignmentReportRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.WithdrawSubmittedAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpGet("work-assignment-reports/{id}/logs")]
    public async Task<IActionResult> GetLogs([FromRoute] string id, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.GetLogsAsync(id, actorUserId, ct);
        return Ok(rs);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
