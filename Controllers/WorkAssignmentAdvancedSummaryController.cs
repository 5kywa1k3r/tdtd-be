using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.Controllers;

// 30/09/2026: giữ mã cũ, chặn toàn bộ entry point; dùng Aggregate v2 trong báo cáo.
[tdtd_be.Services.WorkAssignments.LegacyAggregateDisabled]
[ApiController]
[Authorize]
[Route("api/work-assignment-advanced-summary")]
public sealed class WorkAssignmentAdvancedSummaryController : ControllerBase
{
    private readonly IWorkAssignmentAdvancedSummaryConfigService _configs;
    private readonly IWorkAssignmentAdvancedSummaryHierarchyService _hierarchy;
    private readonly IStatRunCandidateActivation _candidateActivation;

    public WorkAssignmentAdvancedSummaryController(
        IWorkAssignmentAdvancedSummaryConfigService configs,
        IWorkAssignmentAdvancedSummaryHierarchyService hierarchy,
        IStatRunCandidateActivation candidateActivation)
    {
        _configs = configs;
        _hierarchy = hierarchy;
        _candidateActivation = candidateActivation;
    }

    // Native result paths have independent typed snapshots; scalar hierarchy
    // routes keep their guard until their consumers understand native output.
    [HttpPost("native-summary")]
    public async Task<IActionResult> NativeSummary([FromBody] JsonElement body, CancellationToken ct)
        => Ok(await _configs.GetNativeSummaryAsync(StatConfigCanonicalJson.DeserializeStrict<AdvancedNativeSummaryRequest>(body), ct));

    [HttpPost("native-comparison")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> NativeComparison([FromBody] JsonElement body, CancellationToken ct)
    {
        try { return Ok(await _configs.CompareNativeSummaryAsync(StatConfigCanonicalJson.DeserializeStrict<AdvancedNativeComparisonRequest>(body), ct)); }
        catch (AdvancedNativeComparisonBudgetException ex)
        {
            Response.Headers["Retry-After"] = "5";
            return StatusCode(ex.Message == "ADVANCED_NATIVE_COMPARISON_BUSY" ? 429 : 503, new { code = ex.Message });
        }
    }

    [HttpPost("native-refresh")]
    public async Task<IActionResult> NativeRefresh([FromBody] JsonElement body, CancellationToken ct)
        => Ok(await _configs.QueueNativeRefreshAsync(StatConfigCanonicalJson.DeserializeStrict<AdvancedNativeSummaryRequest>(body), ct));

    [HttpPost("native-comparison-records")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> RecordNativeComparison([FromBody] JsonElement body, CancellationToken ct)
    {
        try { return Ok(await _configs.RecordNativeComparisonAsync(StatConfigCanonicalJson.DeserializeStrict<AdvancedNativeComparisonRequest>(body), ct)); }
        catch (AdvancedNativeComparisonBudgetException ex) {
            Response.Headers["Retry-After"] = "5";
            return StatusCode(ex.Message == "ADVANCED_NATIVE_COMPARISON_BUSY" ? 429 : 503, new { code = ex.Message });
        }
    }

    [HttpGet("native-comparison-records/{id}")]
    public async Task<IActionResult> ReadNativeComparisonRecord(string id, [FromQuery] string hash, CancellationToken ct)
        => Ok(await _configs.ReadNativeComparisonRecordAsync(id, hash, ct));

    [HttpGet("native-comparison-records")]
    public async Task<IActionResult> ListNativeComparisonRecords([FromQuery] string scopeId, [FromQuery] string formId,
        [FromQuery] string? after, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        try { return Ok(await _configs.ListNativeComparisonRecordsAsync(scopeId, formId, after, limit, ct)); }
        catch (MongoDB.Driver.MongoExecutionTimeoutException) {
            return StatusCode(503, new { code = "ADVANCED_NATIVE_COMPARISON_LIST_TIMEOUT" });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return StatusCode(503, new { code = "ADVANCED_NATIVE_COMPARISON_LIST_TIMEOUT" });
        }
    }

    [HttpGet("native-comparison-records/{id}/diagnostic")]
    public async Task<IActionResult> DiagnoseNativeComparisonRecord(string id, [FromQuery] string hash, CancellationToken ct)
    {
        try { return Ok(await _configs.DiagnoseNativeComparisonRecordAsync(id, hash, ct)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return StatusCode(503, new { code = "ADVANCED_NATIVE_COMPARISON_DIAGNOSTIC_TIMEOUT" });
        }
    }

    [HttpGet("native-refresh/by-command/{commandId}")]
    public async Task<IActionResult> NativeRefreshByCommand([FromRoute] string commandId, CancellationToken ct)
        => Ok(await _configs.ReadNativeRefreshByCommandAsync(commandId, ct));

    [HttpGet("native-refresh/{refreshId}")]
    public async Task<IActionResult> NativeRefreshStatus(string refreshId, CancellationToken ct)
        => Ok(await _configs.ReadNativeRefreshAsync(refreshId, ct));

    [HttpPost("native-refresh/{refreshId}/retry")]
    public async Task<IActionResult> NativeRefreshRetry(string refreshId, CancellationToken ct)
        => Ok(await _configs.RetryNativeRefreshAsync(refreshId, ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config")]
    public async Task<IActionResult> GetConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        CancellationToken ct)
        => Ok(await _configs.GetP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/versions")]
    public async Task<IActionResult> ListConfigVersions(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        CancellationToken ct)
        => Ok(await _configs.ListP8ConfigVersionsAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/versions/{versionNo:int}")]
    public async Task<IActionResult> GetConfigVersion(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromRoute] int versionNo,
        CancellationToken ct)
        => Ok(await _configs.GetP8ConfigVersionAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            versionNo,
            ct));

    [HttpPut("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config")]
    public async Task<IActionResult> PutConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _configs.PutP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            body,
            ct));

    [HttpPost("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/lock")]
    public async Task<IActionResult> LockP8Config(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _configs.LockP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            body,
            ct));

    [HttpPost("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/next-draft")]
    public async Task<IActionResult> CreateNextDraft(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _configs.CreateNextP8DraftAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            body,
            ct));

    [HttpPost("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/archive")]
    public async Task<IActionResult> ArchiveConfig(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Ok(await _configs.ArchiveP8ConfigAsync(
            assignmentId,
            dynamicFormTemplateId,
            sectionId,
            body,
            ct));

    [HttpGet("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/configs")]
    public async Task<IActionResult> ListConfigs(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        CancellationToken ct)
    {
        _ = GetActorUserId();
        throw AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                reason = "ADVANCED_SUMMARY_LEGACY_CONFIG_ROUTE_BLOCKED"
            });
    }

    [HttpPut("assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/draft")]
    public async Task<IActionResult> SaveDraft(
        [FromRoute] string assignmentId,
        [FromRoute] string dynamicFormTemplateId,
        [FromRoute] string sectionId,
        [FromBody] SaveWorkAssignmentAdvancedSummaryDraftRequest req,
        CancellationToken ct)
    {
        _ = GetActorUserId();
        throw AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                reason = "ADVANCED_SUMMARY_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE"
            });
    }

    [HttpPost("configs/{configId}/lock")]
    public async Task<IActionResult> LockConfig(
        [FromRoute] string configId,
        [FromBody] LockWorkAssignmentAdvancedSummaryConfigRequest req,
        CancellationToken ct)
    {
        _ = GetActorUserId();
        throw AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                reason = "ADVANCED_SUMMARY_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE"
            });
    }

    [HttpPost("configs/{configId}/preview")]
    public async Task<IActionResult> RequestPreview(
        [FromRoute] string configId,
        [FromBody] PreviewWorkAssignmentAdvancedSummaryConfigRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedBuild);
        var result = await _configs.RequestPreviewAsync(
            configId,
            req,
            GetActorUserId(),
            ct);
        return Accepted(result);
    }

    [HttpPost("configs/{configId}/hierarchy/day/{dayKey}/build")]
    public async Task<IActionResult> BuildDayNode(
        [FromRoute] string configId,
        [FromRoute] string dayKey,
        [FromBody] BuildWorkAssignmentAdvancedSummaryDayNodeRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedBuild);
        var result = await _hierarchy.RequestDayNodeBuildAsync(
            configId,
            dayKey,
            req,
            GetActorUserId(),
            ct);
        return Accepted(result);
    }

    [HttpPost("configs/{configId}/hierarchy/month/{monthKey}/build")]
    public async Task<IActionResult> BuildMonthNode(
        [FromRoute] string configId,
        [FromRoute] string monthKey,
        [FromBody] BuildWorkAssignmentAdvancedSummaryMonthNodeRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedBuild);
        var result = await _hierarchy.RequestMonthNodeBuildAsync(
            configId,
            monthKey,
            req,
            GetActorUserId(),
            ct);
        return Accepted(result);
    }

    [HttpPost("configs/{configId}/hierarchy/year/{yearKey}/build")]
    public async Task<IActionResult> BuildYearNode(
        [FromRoute] string configId,
        [FromRoute] string yearKey,
        [FromBody] BuildWorkAssignmentAdvancedSummaryYearNodeRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedBuild);
        var result = await _hierarchy.RequestYearNodeBuildAsync(
            configId,
            yearKey,
            req,
            GetActorUserId(),
            ct);
        return Accepted(result);
    }

    [HttpPost("configs/{configId}/hierarchy/query")]
    public async Task<IActionResult> QueryHierarchy(
        [FromRoute] string configId,
        [FromBody] QueryWorkAssignmentAdvancedSummaryHierarchyRequest req,
        CancellationToken ct)
    {
        _candidateActivation.RequireCapability(
            StatRunCapabilities.AdvancedSummary,
            StatRunRouteRegistry.AdvancedResult);
        var result = await _hierarchy.QueryHierarchyAsync(
            configId,
            req,
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
