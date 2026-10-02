using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/aggregate-v1")]
[Route("api/aggregate-v2")]
public sealed class AggregateMappingPreviewController(MongoDbContext db, IWorkReportPayloadReader payloads, IConfiguration configuration) : ControllerBase
{
    // v2 is explicitly activated; legacy v1 still obeys its original phase barrier.
    private bool V2Enabled => AggregateIntegrationGate.IsV2(Request) && AggregateIntegrationGate.V2Ready(configuration);
    private bool IntegrationReady => AggregateIntegrationGate.IsV2(Request) ? V2Enabled : AggregateIntegrationGate.Ready(configuration);
    [HttpPost("editor/bootstrap")]
    public async Task<IActionResult> Bootstrap(AggregateEditorBootstrapDto request, CancellationToken ct)
    {
        if (!AggregateIntegrationGate.IsV2(Request)) StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Run, "AGGREGATE_MAPPING_SOURCE_READ");
        if (!IntegrationReady) return Conflict(Error("AGG_PREVIEW_INTEGRATION_PENDING"));
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var session = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(session)) return Unauthorized(Error("AGG_AUTH_REQUIRED"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var reader = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), V2Enabled);
            var initial = request.ViewContext is { View: not null } view ? await reader.ReadContextAsync(view, actor, timeout.Token) : await reader.BootstrapAsync(request.ReportId!, actor, timeout.Token);
            var authority = await new AggregateMongoCommandReader(db, payloads, V2Enabled).AuthorizeAsync(initial.Context, actor, session, timeout.Token);
            var read = authority.Read;
            var capabilities = new {
                readConfig = AggregateMappingPolicy.Decide(AggregateAction.ReadConfig, read.Authority),
                editConfig = AggregateMappingPolicy.Decide(AggregateAction.EditConfig, read.Authority),
                editMapping = AggregateMappingPolicy.Decide(AggregateAction.EditMapping, read.Authority),
                listPipeline = new { supported = true, version = 1, recipeVersion = 2, semanticProfile = "REPORT_MAPPING_LIST_V1",
                    maxTopN = 200, maxPageSize = 50, maxScannedRecords = 40_000, maxRetainedSelectionBytes = 16_777_216,
                    maxTargetRecords = 200, directListSnapshotScan = true, sortChoiceBy = new[] { "CODE" },
                    allSelectionBudgeted = true, emptyMultiChoicePolicy = "BLANK", blankChoiceOption = "IS_BLANK",
                    negativePredicatesMatchBlank = false, requiredChoiceOnSubmit = true, tableBridge = false }
            };
            Response.Headers.CacheControl = "no-store";
            return Ok(new { read.Context, read.Revisions, read.DataWindow, Capabilities = capabilities,
                Target = await reader.EditorSchemaAsync(read, read.TargetSchema.Pin, actor, timeout.Token) });
        }
        catch (AggregatePreviewException ex) { return StatusCode(409, Error(ex.Code)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(503, Error("AGG_PREVIEW_TIMEOUT")); }
    }
    [HttpPost("editor/source-schema")]
    public Task<IActionResult> SourceSchema(AggregateEditorSchemaQueryDto request, CancellationToken ct)
        => ReadQuery(request.Context, async (reader, context, actor, token) =>
            await reader.EditorSchemaAsync(context, request.Form, actor, token, includeReportOptions: true), ct);
    [HttpPost("editor/list-options")]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> ListOptions(AggregateListOptionsQueryDto request, CancellationToken ct)
        => ReadQuery(request.Context, (reader, context, actor, token) => reader.ListOptionsAsync(context, request, actor, token), ct);
    [HttpPost("instances/{instanceId}/preview")]
    [RequestSizeLimit(524288)]
    public async Task<IActionResult> Preview(string instanceId, [FromBody] AggregatePreviewRequestDto request, CancellationToken ct)
    {
        // Publishing v2 must not release the legacy P9/Flow lane.
        if (!AggregateIntegrationGate.IsV2(Request)) StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Run, "AGGREGATE_MAPPING_PREVIEW");
        if (!IntegrationReady) return Conflict(Error("AGG_PREVIEW_INTEGRATION_PENDING"));
        if (request.InstanceId != instanceId) return BadRequest(Error("AGG_CONTEXT_STALE"));
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized(Error("AGG_AUTH_REQUIRED"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var reader = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), V2Enabled);
            var result = await new AggregatePreviewService(reader, new AggregateContentTableStore(db), new AggregateListStore(db)).PreviewAsync(request, actor, timeout.Token);
            Response.Headers.CacheControl = "no-store";
            return Ok(AggregateListTransport.Compact(result));
        }
        catch (AggregatePreviewException ex)
        {
            var status = ex.Code.Contains("UNAVAILABLE", StringComparison.Ordinal) ? 404
                : ex.Code is "AGG_INPUT_STALE" or "AGG_REVISION_CONFLICT" or "AGG_CONTEXT_STALE" ? 409
                : ex.Code == "AGG_PREVIEW_FORBIDDEN" ? 403 : ex.Code == "AGG_BUDGET_EXCEEDED" ? 422 : 400;
            return StatusCode(status, Error(ex.Code, ex.Path));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return StatusCode(503, Error("AGG_PREVIEW_TIMEOUT")); }
    }
    [HttpPost("source-forms/query")]
    public Task<IActionResult> SourceForms([FromBody] AggregateSourceFormsQueryDto request, CancellationToken ct)
        => ReadQuery(request.Context, async (reader, context, actor, token) => await reader.ListSourceFormsAsync(context, actor, token), ct);

    [HttpPost("sources/query")]
    public Task<IActionResult> Sources([FromBody] AggregateDraftSourceQueryDto request, CancellationToken ct)
        => ReadQuery(request.Context, async (reader, context, actor, token) =>
        {
            if (request.PageSize is < 1 or > 100) throw new AggregatePreviewException("AGG_PAGE_SIZE_INVALID");
            _ = await reader.ReadSchemaAsync(request.Form, context, actor, token);
            var listing = await reader.ListPickerSourcesAsync(context, request.Form, actor, token);
            var fingerprint = AggregateDigest.Of(new { actor, context.AuthorizationFingerprint, context.Context, request.Form, listing.MembershipRevision });
            var offset = 0;
            if (request.Cursor != null)
            {
                var parts = request.Cursor.Split(':');
                if (parts.Length != 2 || parts[0] != fingerprint || !int.TryParse(parts[1], out offset) || offset < 0)
                    throw new AggregatePreviewException("AGG_CURSOR_STALE");
            }
            var visible = listing.Headers.Where(h => h.WholeReportReadable).OrderBy(h => h.Pin.ReportId, StringComparer.Ordinal).ToArray();
            if (offset > visible.Length) throw new AggregatePreviewException("AGG_CURSOR_STALE");
            var items = visible.Skip(offset).Take(request.PageSize).Select(h => h.Pin).ToList();
            var next = offset + items.Count;
            return new AggregateSourceSearchResponseDto(items, next < visible.Length ? fingerprint + ":" + next : null, "NOT_RESOLVED", []);
        }, ct);

    [HttpPost("contexts/resolve")]
    public async Task<IActionResult> ResolveContext([FromBody] AggregateResolveContextRequestDto request, CancellationToken ct)
    {
        var selector = new AggregatePeriodContextDto("ONCE", request.WorkId, request.AssignmentId, request.BindingId,
            request.ReportId, request.WorkReportPeriodId, null, null, null, null, null, "");
        return await ReadQuery(selector, (_, context, _, _) => Task.FromResult<object>(new { context.Context, context.Revisions, context.DataWindow }), ct, compareContext: false);
    }
    private async Task<IActionResult> ReadQuery(AggregatePeriodContextDto selector,
        Func<AggregateMongoPreviewReader, AggregateReadContext, string, CancellationToken, Task<object>> read, CancellationToken ct, bool compareContext = true)
    {
        if (!AggregateIntegrationGate.IsV2(Request)) StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Run, "AGGREGATE_MAPPING_SOURCE_READ");
        if (!IntegrationReady) return Conflict(Error("AGG_PREVIEW_INTEGRATION_PENDING"));
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized(Error("AGG_AUTH_REQUIRED"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var reader = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), V2Enabled);
            var context = await reader.ReadContextAsync(selector, actor, timeout.Token);
            if (compareContext && AggregatePeriodContextContract.Compare(selector, context.Context) is { } issue)
                throw new AggregatePreviewException(issue.Code);
            Response.Headers.CacheControl = "no-store";
            return Ok(await read(reader, context, actor, timeout.Token));
        }
        catch (AggregatePreviewException ex) { return StatusCode(ex.Code.Contains("UNAVAILABLE", StringComparison.Ordinal) ? 404 : 409, Error(ex.Code)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(503, Error("AGG_PREVIEW_TIMEOUT")); }
    }
    private AggregateErrorDto Error(string code, string path = "$")
        => new(code, HttpContext.TraceIdentifier, [new(code, path, "Preview could not be resolved; no report data was written.")], code is "AGG_INPUT_STALE" or "AGG_PREVIEW_TIMEOUT");
}
