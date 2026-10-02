using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Data;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

public sealed record AggregateContentReadRequest(string ReportId, string? TableId, long? PayloadRevision,
    string? JobId, JsonElement Reference, int Offset = 0, int Limit = 20, string? RowKey = null, int Part = 0, string? ViewId = null, string? ListReadId = null);

[ApiController, Authorize, Route("api/aggregate-v2/content"), RequestSizeLimit(16384)]
public sealed class AggregateContentController(MongoDbContext db, IWorkReportPayloadReader payloads,
    IWorkAssignmentReportService reports, IConfiguration config) : ControllerBase
{
    [HttpPost("page")]
    public Task<IActionResult> Page(AggregateContentReadRequest request, CancellationToken ct) => Read(request, false, ct);
    [HttpPost("part")]
    public Task<IActionResult> Part(AggregateContentReadRequest request, CancellationToken ct) => Read(request, true, ct);
    private async Task<IActionResult> Read(AggregateContentReadRequest request, bool textPart, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AggregateIntegrationGate.V2Ready(config)) return Conflict(new { code = "AGG_ACTIVATION_REQUIRED" });
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var session = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(session)) return Unauthorized();
        try
        {
            var targetId = request.ViewId ?? request.ReportId;
            if (request.ViewId != null && !string.IsNullOrEmpty(request.ReportId)) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
            var reference = request.Reference.Deserialize<AggregateContentReference>(AggregateCanonical.Json)
                ?? throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
            if (!string.IsNullOrEmpty(request.JobId))
                await new AggregatePreviewJobs(db, payloads, config).AuthorizeContent(request.JobId, targetId, reference, actor, session, ct);
            else if (request.ViewId != null)
            {
                await new AggregateListReadScopes(db, payloads).Authorize(new("", null, reference.Id, reference.Hash,
                    ReadId: request.ListReadId, ViewId: request.ViewId), actor, session, ct);
            }
            else
            {
                var report = await reports.AuthorizeContentReadAsync(request.ReportId, actor, ct);
                if (request.PayloadRevision != report.PayloadRevision) throw new AggregatePreviewException("AGG_INPUT_STALE");
                var snapshot = await payloads.LoadReportPayloadAsync(report, ct);
                WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, snapshot);
                using var doc = JsonDocument.Parse(snapshot.TableValuesJson ?? "{}");
                if (!doc.RootElement.TryGetProperty("nativeTables", out var native)
                    || !native.GetProperty("tables").EnumerateArray().Any(t => t.GetProperty("tableId").GetString() == request.TableId
                        && t.TryGetProperty("contentRef", out var value) && value.Deserialize<AggregateContentReference>(AggregateCanonical.Json) == reference))
                    throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
            }
            var store = new AggregateContentTableStore(db);
            if(request.ViewId == null && string.IsNullOrEmpty(request.JobId))try { await AggregateContentSnapshotJob.Schedule(db,HttpContext.RequestServices.GetService<Hangfire.IBackgroundJobClient>(),request.ReportId,ct); }
                catch(Exception ex) { HttpContext.RequestServices.GetRequiredService<ILogger<AggregateContentController>>().LogWarning(ex,"Content snapshot dispatch remains pending"); }
            return Ok(textPart ? (object)await store.TextPart(targetId, reference, request.RowKey ?? "", request.Part, ct)
                : await store.Page(targetId, reference, request.Offset, request.Limit, ct));
        }
        catch (AggregatePreviewException ex) { return StatusCode(ex.Code.Contains("FORBIDDEN") ? 403 : ex.Code.Contains("UNAVAILABLE") ? 404 : 409, new { code = ex.Code }); }
        catch (JsonException) { return BadRequest(new { code = "AGG_CONTENT_TABLE_INVALID" }); }
    }
}
