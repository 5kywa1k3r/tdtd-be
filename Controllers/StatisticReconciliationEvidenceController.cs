using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;

namespace tdtd_be.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationEvidenceCreateHttpRequest
{
    public string CommandId { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public bool IncludeOperatorDetail { get; set; }
}

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/works/{workId}/statistics/{scopeAssignmentId}/reconciliations")]
public sealed class StatisticReconciliationEvidenceController(
    MeAccessor me,
    IStatisticReconciliationEvidenceOwner owner) : ControllerBase
{
    [HttpGet("{reconciliationId}/evidence-exports")]
    public async Task<IActionResult> ListAsync(string workId,
        string scopeAssignmentId, string reconciliationId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        // Current assignment authorization precedes artifact lookup and page
        // validation, preserving hidden/missing parity after access removal.
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            return Ok(await owner.ListAsync(authorization,
                reconciliationId, page, pageSize, ct));
        }
        catch (StatisticReconciliationEvidenceException error)
        {
            return EvidenceError(error);
        }
    }

    [HttpGet("{reconciliationId}/evidence-exports/{exportId}")]
    public async Task<IActionResult> ReadAsync(string workId,
        string scopeAssignmentId, string reconciliationId, string exportId,
        CancellationToken ct)
    {
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            return Ok(await owner.ReadAsync(authorization,
                reconciliationId, exportId, ct));
        }
        catch (StatisticReconciliationEvidenceException error)
        {
            return EvidenceError(error);
        }
    }

    [HttpPost("{reconciliationId}/evidence-exports")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<IActionResult> CreateAsync(string workId,
        string scopeAssignmentId, string reconciliationId,
        CancellationToken ct)
    {
        // Scope authorization precedes command-body parsing so an unauthorized
        // actor cannot use validation differences as an existence oracle.
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            var root = await StatisticReconciliationBoundedJsonBody.ReadAsync(
                Request.Body, ct);
            var body = StatisticReconciliationCanonicalJson.DeserializeStrict<
                StatisticReconciliationEvidenceCreateHttpRequest>(root);
            var result = await owner.CreateAsync(authorization,
                reconciliationId,
                new(body.CommandId, body.Format,
                    body.IncludeOperatorDetail, authorization.ActorId,
                    authorization.PermissionSnapshotSha256,
                    authorization.CanViewOperatorDetail,
                    DateTime.UnixEpoch, TimeSpan.FromDays(30)), ct);
            return Ok(result);
        }
        catch (InvalidOperationException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Evidence export request is invalid.");
        }
        catch (StatisticReconciliationEvidenceException error)
        {
            return EvidenceError(error);
        }
    }

    [HttpGet("{reconciliationId}/evidence-exports/{exportId}/download")]
    public async Task<IActionResult> DownloadAsync(string workId,
        string scopeAssignmentId, string reconciliationId, string exportId,
        CancellationToken ct)
    {
        // Re-authorize on every download. The artifact is not looked up until
        // the current assignment scope has passed authorization.
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            var result = await owner.DownloadAsync(authorization,
                reconciliationId, exportId, ct);
            Response.Headers.Append("X-Content-SHA256",
                result.Artifact.ContentSha256);
            Response.Headers.Append("X-Manifest-SHA256",
                result.Artifact.ManifestSha256);
            return File(result.Content, result.Artifact.ContentType,
                result.Artifact.FileName, enableRangeProcessing: false);
        }
        catch (StatisticReconciliationEvidenceException error)
        {
            return EvidenceError(error);
        }
    }

    private IActionResult EvidenceError(
        StatisticReconciliationEvidenceException error)
        => error.Code switch
        {
            StatisticReconciliationEvidenceFailureCodes.PermissionDenied =>
                OpaqueStatus(StatusCodes.Status404NotFound),
            StatisticReconciliationEvidenceFailureCodes.Expired =>
                StatusCode(StatusCodes.Status410Gone),
            StatisticReconciliationEvidenceFailureCodes.ScopeLimit =>
                Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Evidence export exceeds a deterministic limit."),
            StatisticReconciliationEvidenceFailureCodes.InvalidRequest =>
                Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Evidence export request is invalid."),
            _ => Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Evidence export could not be proven from current state.")
        };

    private IActionResult OpaqueStatus(int statusCode)
    {
        Response.StatusCode = statusCode;
        return new EmptyResult();
    }
}
