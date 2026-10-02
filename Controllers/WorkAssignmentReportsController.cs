using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Services.StatisticsConfiguration;
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

    [HttpGet("work-assignment-reports/{id}/fields/{fieldId}/enum-options")]
    public async Task<IActionResult> SearchFieldEnumOptions(string id, string fieldId, [FromQuery] string catalogId,
        [FromQuery] string? q, [FromQuery] int page = 0, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await _service.SearchFieldEnumOptionsAsync(id, null, fieldId, catalogId, q, page, pageSize, GetActorUserId(), ct));

    [HttpGet("work-assignment-reports/{id}/selected-enum-labels")]
    public async Task<IActionResult> GetSelectedEnumLabels(string id, CancellationToken ct)
        => Ok(await _service.GetSelectedEnumLabelsAsync(id, GetActorUserId(), ct));

    [HttpGet("work-assignments/{assignmentId}/report-fields/{fieldId}/enum-options")]
    public async Task<IActionResult> SearchAssignmentFieldEnumOptions(string assignmentId, string fieldId, [FromQuery] string catalogId,
        [FromQuery] string? q, [FromQuery] int page = 0, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await _service.SearchFieldEnumOptionsAsync(null, assignmentId, fieldId, catalogId, q, page, pageSize, GetActorUserId(), ct));

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
        return rs.LifecycleProjectionPending ? Accepted(rs) : Ok(rs);
    }

    [HttpPatch("work-assignment-reports/{id}/draft/patch")]
    public async Task<IActionResult> SaveDraftPatch([FromRoute] string id, [FromBody] SaveWorkAssignmentReportDraftPatchRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.SaveDraftPatchAsync(id, req, actorUserId, ct);
        return rs.LifecycleProjectionPending ? Accepted(rs) : Ok(rs);
    }

    // Luồng tổng hợp cũ tạm khóa; giữ action để đối chiếu.
    [tdtd_be.Services.WorkAssignments.LegacyAggregateDisabled]
    [HttpPost("work-assignment-reports/{id}/draft/apply-dynamic-form-aggregate")]
    public async Task<IActionResult> ApplyDynamicFormAggregateDraft([FromRoute] string id, [FromBody] ApplyDynamicFormAggregateDraftRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Run);
        var rs = await _service.ApplyDynamicFormAggregateDraftAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    // Luồng tổng hợp cũ tạm khóa; giữ action để đối chiếu.
    [tdtd_be.Services.WorkAssignments.LegacyAggregateDisabled]
    [HttpPost("work-assignment-reports/{id}/draft/preview-dynamic-form-aggregate")]
    public async Task<IActionResult> PreviewDynamicFormAggregateDraft([FromRoute] string id, [FromBody] ApplyDynamicFormAggregateDraftRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Run);
        var rs = await _service.PreviewDynamicFormAggregateDraftAsync(id, req, actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/draft/preview-dynamic-flow-mapping")]
    [NonAction] // Flow chưa mở trong bản deploy này.
    [ProducesResponseType(typeof(DynamicFlowMappingPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DynamicFlowMappingPreviewResponse>> PreviewDynamicFlowMapping(
        [FromRoute] string id,
        [FromBody] DynamicFlowMappingRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.PreviewDynamicFlowMappingAsync(id, req ?? new DynamicFlowMappingRequest(), actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/draft/apply-dynamic-flow-mapping")]
    [NonAction] // Flow chưa mở trong bản deploy này.
    [ProducesResponseType(typeof(WorkAssignmentReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(AppErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<WorkAssignmentReportResponse>> ApplyDynamicFlowMapping(
        [FromRoute] string id,
        [FromBody] DynamicFlowMappingRequest req,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.ApplyDynamicFlowMappingAsync(id, req ?? new DynamicFlowMappingRequest(), actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/submit")]
    public async Task<IActionResult> Submit([FromRoute] string id, [FromBody] SubmitWorkAssignmentReportRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        req.AggregateSessionKey = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        var rs = await _service.SubmitAsync(id, req, actorUserId, ct);
        return rs.LifecycleProjectionPending ? Accepted(rs) : Ok(rs);
    }

    [HttpPost("work-assignment-reports/{id}/withdraw-submitted")]
    public async Task<IActionResult> WithdrawSubmitted([FromRoute] string id, [FromBody] ReturnWorkAssignmentReportRequest req, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _service.WithdrawSubmittedAsync(id, req, actorUserId, ct);
        return rs.LifecycleProjectionPending ? Accepted(rs) : Ok(rs);
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
