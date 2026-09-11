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
