using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/internal/statistic-reconciliation/actual")]
public sealed class StatisticReconciliationActualOwnerController : ControllerBase
{
    private const long MaximumBodyBytes =
        StatisticReconciliationActualJson.MaxJsonUtf8Bytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions PageJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        MaxDepth = 128,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly StatisticReconciliationActualApiEndpointService _endpoint;

    public StatisticReconciliationActualOwnerController(
        MongoDbContext context,
        MeAccessor me,
        IStatisticReconciliationActualPinnedDirectResultService directResults,
        IWorkReportStatisticDiffService diffResults,
        IWorkAssignmentBasicSummaryService basicResults)
        : this(new StatisticReconciliationActualApiProjectionService(
            context,
            me,
            directResults,
            diffResults,
            basicResults as IStatisticReconciliationActualBasicApiReadOwner
            ?? throw new InvalidOperationException(
                "The Basic summary service has no P10 read-only API owner.")))
    {
    }

    internal StatisticReconciliationActualOwnerController(
        StatisticReconciliationActualApiEndpointService endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    [HttpGet("authorize")]
    public async Task<IActionResult> AuthorizeTarget(
        [FromQuery] string? workId,
        [FromQuery(Name = "scopeId")] string? scopeAssignmentId,
        CancellationToken cancellationToken)
    {
        var authorization = await _endpoint.AuthorizeBeforeExistenceAsync(
            workId,
            scopeAssignmentId,
            cancellationToken);
        if (authorization is null || !authorization.IsAuthorized)
            return NotFound();

        // IsAuthorized is deliberately not emitted: the HTTP owner treats a
        // hidden 404 as denial and accepts only this exact success wire shape.
        return Ok(new
        {
            authorization.ActorUserId,
            authorization.WorkId,
            authorization.ScopeAssignmentId,
            authorization.PermissionCodes,
            authorization.RowCountBeforeRedaction,
            authorization.RowCountAfterRedaction,
            authorization.AuthorizationSnapshotSha256
        });
    }

    [HttpPost("page")]
    [RequestSizeLimit(MaximumBodyBytes)]
    public async Task<IActionResult> ReadPage(
        [FromQuery] string? workId,
        [FromQuery(Name = "scopeId")] string? scopeAssignmentId,
        CancellationToken cancellationToken)
    {
        // Business authorization is intentionally completed from query-only
        // target pins before the request body is read or parsed.
        var authorization = await _endpoint.AuthorizeBeforeExistenceAsync(
            workId,
            scopeAssignmentId,
            cancellationToken);
        if (authorization is null || !authorization.IsAuthorized)
            return NotFound();

        try
        {
            var query = await ParsePageQueryAsync(cancellationToken);
            var page = await _endpoint.ReadPageProjectionAsync(
                authorization,
                query,
                cancellationToken);
            return Ok(page);
        }
        catch (StatisticReconciliationActualApiEndpointException error)
        {
            return ProtocolError(error.StatusCode, error.Reason);
        }
        catch (StatisticReconciliationActualObservationException error)
        {
            return ProtocolError(StatusCodes.Status400BadRequest, error.Reason);
        }
        catch (JsonException)
        {
            return ProtocolError(
                StatusCodes.Status400BadRequest,
                "API_PAGE_BODY_INVALID");
        }
        catch (DecoderFallbackException)
        {
            return ProtocolError(
                StatusCodes.Status400BadRequest,
                "API_PAGE_BODY_UTF8_INVALID");
        }
    }

    private async Task<StatisticReconciliationActualApiOwnerPageQuery>
        ParsePageQueryAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength is < 1)
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status400BadRequest,
                "API_PAGE_BODY_REQUIRED");
        if (Request.ContentLength is > MaximumBodyBytes)
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status413PayloadTooLarge,
                "API_PAGE_BODY_TOO_LARGE");
        var mediaType = Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.Ordinal) &&
            !(mediaType?.EndsWith("+json", StringComparison.Ordinal) ?? false))
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status415UnsupportedMediaType,
                "API_PAGE_CONTENT_TYPE_INVALID");
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await Request.Body.ReadAsync(
                chunk.AsMemory(0, chunk.Length),
                cancellationToken);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaximumBodyBytes)
            {
                throw new StatisticReconciliationActualApiEndpointException(
                    StatusCodes.Status413PayloadTooLarge,
                    "API_PAGE_BODY_TOO_LARGE");
            }
            await buffer.WriteAsync(
                chunk.AsMemory(0, read),
                cancellationToken);
        }
        if (total == 0)
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status400BadRequest,
                "API_PAGE_BODY_REQUIRED");
        }

        var raw = StrictUtf8.GetString(buffer.ToArray());
        using var document = StatisticReconciliationActualJson.ParseStrict(
            raw,
            "API_PAGE_BODY");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status400BadRequest,
                "API_PAGE_BODY_NOT_OBJECT");
        }
        return document.RootElement.Deserialize<
                   StatisticReconciliationActualApiOwnerPageQuery>(
                   PageJsonOptions)
               ?? throw new StatisticReconciliationActualApiEndpointException(
                   StatusCodes.Status400BadRequest,
                   "API_PAGE_BODY_REQUIRED");
    }

    private ObjectResult ProtocolError(int statusCode, string reason)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = "Statistic reconciliation actual API owner rejected the request.",
            Type = "about:blank",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["reason"] = reason;
        return StatusCode(statusCode, problem);
    }
}
