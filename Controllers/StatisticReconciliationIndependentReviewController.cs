using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

namespace tdtd_be.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationReviewSubmitHttpRequest
{
    public string CommandId { get; set; } = string.Empty;
    public string Gate { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public long ExpectedStateRevision { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationReviewSupersedeHttpRequest
{
    public string CommandId { get; set; } = string.Empty;
    public string PreviousGenerationId { get; set; } = string.Empty;
    public long ExpectedStateRevision { get; set; }
}

/// <summary>
/// Candidate-only review surface. Scope authorization completes before a POST
/// body is read, and the wire request contains no expected, actual, delta,
/// freshness, permission, verdict, generation, evidence, or Mongo material.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/works/{workId}/statistics/{scopeAssignmentId}/reconciliations")]
public sealed class StatisticReconciliationIndependentReviewController(
    MeAccessor me,
    IStatisticReconciliationIndependentReviewOwner owner) : ControllerBase
{
    private const long MaximumBodyBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [HttpPost("{reconciliationId}/review-decisions")]
    [RequestSizeLimit(MaximumBodyBytes)]
    public async Task<IActionResult> SubmitAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
    {
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            var body = await ReadBodyAsync<
                StatisticReconciliationReviewSubmitHttpRequest>(ct);
            var result = await owner.SubmitAsync(authorization,
                reconciliationId,
                new StatisticReconciliationReviewOwnerCommand(body.CommandId,
                    body.Gate, body.Decision, body.ExpectedStateRevision), ct);
            return Ok(result);
        }
        catch (StatisticReconciliationIndependentReviewException error)
        {
            return ReviewError(error);
        }
    }

    [HttpGet("{reconciliationId}/review-decisions")]
    public async Task<IActionResult> ReadAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
    {
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            return Ok(await owner.ReadAsync(authorization,
                reconciliationId, ct));
        }
        catch (StatisticReconciliationIndependentReviewException error)
        {
            return ReviewReadError(error);
        }
    }

    [HttpGet("{reconciliationId}/review-final-approval")]
    public async Task<IActionResult> FinalApprovalAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
    {
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            return Ok(await owner.GetFinalApprovalAsync(authorization,
                reconciliationId, ct));
        }
        catch (StatisticReconciliationIndependentReviewException error)
        {
            return ReviewError(error);
        }
    }

    [HttpPost("{reconciliationId}/review-supersessions")]
    [RequestSizeLimit(MaximumBodyBytes)]
    public async Task<IActionResult> SupersedeAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
    {
        var authorization = await owner.AuthorizeScopeAsync(workId,
            scopeAssignmentId, me.RequireMe(), ct);
        if (authorization is null)
            return OpaqueStatus(StatusCodes.Status404NotFound);
        try
        {
            var body = await ReadBodyAsync<
                StatisticReconciliationReviewSupersedeHttpRequest>(ct);
            var result = await owner.SupersedeAsync(authorization,
                reconciliationId,
                new StatisticReconciliationReviewOwnerSupersessionCommand(
                    body.CommandId, body.PreviousGenerationId,
                    body.ExpectedStateRevision), ct);
            return Ok(result);
        }
        catch (StatisticReconciliationIndependentReviewException error)
        {
            return ReviewError(error);
        }
    }

    private async Task<T> ReadBodyAsync<T>(CancellationToken ct)
    {
        if (Request.ContentLength is < 1)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "bodyRequired");
        if (Request.ContentLength is > MaximumBodyBytes)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "bodyTooLarge");
        var mediaType = Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json",
                StringComparison.Ordinal) &&
            !(mediaType?.EndsWith("+json", StringComparison.Ordinal) ?? false))
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "contentType");

        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await Request.Body.ReadAsync(chunk.AsMemory(), ct);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaximumBodyBytes)
                throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                    "bodyTooLarge");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        if (total == 0)
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "bodyRequired");
        try
        {
            var raw = StrictUtf8.GetString(buffer.ToArray());
            using var document = JsonDocument.Parse(raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            return StatisticReconciliationCanonicalJson.DeserializeStrict<T>(
                document.RootElement);
        }
        catch (DecoderFallbackException)
        {
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "bodyUtf8");
        }
        catch (JsonException)
        {
            throw StatisticReconciliationIndependentReviewCanonical.Invalid(
                "bodyJson");
        }
    }

    private IActionResult ReviewReadError(
        StatisticReconciliationIndependentReviewException error)
        => error.Code ==
            StatisticReconciliationIndependentReviewFailureCodes
                .TargetNotSignable
            ? TargetNotSignableProblem()
            : ReviewError(error);

    private IActionResult ReviewError(
        StatisticReconciliationIndependentReviewException error)
        => error.Code switch
        {
            StatisticReconciliationIndependentReviewFailureCodes.PermissionDenied =>
                OpaqueStatus(StatusCodes.Status404NotFound),
            StatisticReconciliationIndependentReviewFailureCodes.SeparationOfDuties =>
                OpaqueStatus(StatusCodes.Status403Forbidden),
            StatisticReconciliationIndependentReviewFailureCodes.EvidenceInvalid =>
                Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Independent review request is invalid."),
            _ => Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Independent review state conflict.")
        };

    private static IActionResult TargetNotSignableProblem()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Independent review state conflict."
        };
        problem.Extensions["code"] =
            StatisticReconciliationIndependentReviewFailureCodes
                .TargetNotSignable;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status409Conflict
        };
    }

    private IActionResult OpaqueStatus(int statusCode)
    {
        Response.StatusCode = statusCode;
        return new EmptyResult();
    }
}
