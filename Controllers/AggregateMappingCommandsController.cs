using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/aggregate-v1"), Route("api/aggregate-v2")]
[RequestSizeLimit(524288)]
public sealed class AggregateMappingCommandsController(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner transactions,
    IWorkReportPayloadReader payloadReader, IWorkReportPayloadWriter payloadWriter, IConfiguration configuration) : ControllerBase
{
    [HttpPost("contexts/read")]
    public Task<IActionResult> ReadContext(AggregateInstanceReadCommandDto request, CancellationToken ct)
        => Run("read", "READ", ContextIdentity(request.Context), (s, c) => s.ReadContextAsync(c, request.Context, ct), ct);
    [HttpPost("configs")]
    public Task<IActionResult> CreateConfig(AggregateConfigCreateCommandDto request, CancellationToken ct)
        => Run(request.Config.CommandId, "CREATE_CONFIG", ContextIdentity(request.Context), (s, c) => s.CreateConfigAsync(c, request.Context, request.Config, ct), ct);
    [HttpPost("reports/{id}/submission-preview")]
    public Task<IActionResult> SubmissionPreview(string id, AggregateInstanceReadCommandDto request, CancellationToken ct)
        => Run("preview", "SUBMIT", id, (s, c) => id != request.Context.ReportId
            ? throw new AggregatePreviewException("AGG_CONTEXT_STALE") : s.PreviewSubmissionAsync(c, request.Context, ct), ct);
    [HttpPost("instances")]
    public Task<IActionResult> CreateInstance(AggregateInstanceCreateCommandDto request, CancellationToken ct)
        => Run(request.CommandId, "CREATE_INSTANCE", AggregateTargetIdentity.Id(request.Context)!, (s, c) => s.CreateInstanceAsync(c, request.Context, request.ConfigId, ct), ct);
    [HttpPost("instances/{id}/mapping/queue-preview")]
    public Task<IActionResult> QueuePreview(string id, AggregateMappingPreviewCommandDto request, CancellationToken ct)
        => Run("preview", "QUEUE_MAPPING", id, (s, c) => s.PreviewQueuedMappingAsync(c, request.Context, id, Change(request.Change), ct), ct);
    [HttpPost("instances/{id}/mapping/queue")]
    public async Task<IActionResult> QueueMapping(string id, AggregateMappingApplyCommandDto request, CancellationToken ct)
    {
        var result = await Run(request.CommandId, "QUEUE_MAPPING", id, (s, c) => s.SaveQueuedMappingAsync(c, request.Context, id, Change(request.Change), request.ConfirmationToken, ct), ct);
        if (result is OkObjectResult) await DispatchQueued(request.Context.WorkId, ct);
        return result;
    }
    [HttpPost("configs/{id}/queue-preview")]
    public Task<IActionResult> QueueConfigPreview(string id, AggregateConfigImpactCommandDto request, CancellationToken ct)
        => Run("preview", "QUEUE_CONFIG", id, (s, c) => s.PreviewQueuedConfigAsync(c, request.Context, id, request.Impact, ct), ct);
    [HttpPost("configs/{id}/queue")]
    public async Task<IActionResult> QueueConfig(string id, AggregateConfigRevisionCommandDto request, CancellationToken ct)
    {
        var result = await Run(request.CommandId, "QUEUE_CONFIG", id, (s, c) => s.SaveQueuedConfigAsync(c, request.Context, id, request.Impact, request.ConfirmationToken, ct), ct);
        if (result is OkObjectResult) await DispatchQueued(request.Context.WorkId, ct);
        return result;
    }
    [HttpPost("instances/{id}/computation/cancel")]
    public Task<IActionResult> CancelComputation(string id, AggregateUnlinkPreviewCommandDto request, CancellationToken ct)
        => Run("cancel", "CANCEL_COMPUTATION", id, (s, c) => s.ChangeComputationAsync(c, request.Context, id, request.ExpectedRevision, false, ct), ct);
    [HttpPost("instances/{id}/computation/retry")]
    public async Task<IActionResult> RetryComputation(string id, AggregateUnlinkPreviewCommandDto request, CancellationToken ct)
    {
        var result = await Run("retry", "RETRY_COMPUTATION", id, (s, c) => s.ChangeComputationAsync(c, request.Context, id, request.ExpectedRevision, true, ct), ct);
        if (result is OkObjectResult) await DispatchQueued(request.Context.WorkId, ct);
        return result;
    }
    private async Task DispatchQueued(string workId, CancellationToken ct)
    {
        try { await AggregateMaterializationDispatch.Schedule(db, transactions,
            HttpContext.RequestServices.GetService<Hangfire.IBackgroundJobClient>(), 100, ct, workId); }
        catch (Exception ex) { HttpContext.RequestServices.GetRequiredService<ILogger<AggregateMappingCommandsController>>()
            .LogWarning(ex, "Aggregate computation remains durable after dispatch failure"); }
    }
    [HttpPost("instances/{id}/mapping/preview")]
    public Task<IActionResult> Preview(string id, AggregateMappingPreviewCommandDto request, CancellationToken ct)
        => Run("preview", "APPLY", id, (s, c) => s.PreviewChangeAsync(c, request.Context, id, Change(request.Change), ct), ct);
    [HttpPost("instances/{id}/apply")]
    public async Task<IActionResult> Apply(string id, AggregateMappingApplyCommandDto request, CancellationToken ct)
    {
        var result=await Run(request.CommandId, "APPLY", id, (s, c) => s.ApplyAsync(c, request.Context, id, Change(request.Change), request.ConfirmationToken, ct), ct);
        if(result is OkObjectResult)try { await AggregateContentSnapshotJob.Schedule(db,HttpContext.RequestServices.GetService<Hangfire.IBackgroundJobClient>(),AggregateTargetIdentity.Id(request.Context)!,ct); }
            catch(Exception ex) { HttpContext.RequestServices.GetRequiredService<ILogger<AggregateMappingCommandsController>>().LogWarning(ex,"Content snapshot dispatch pending after committed Apply"); }
        return result;
    }
    [HttpPost("configs/{id}/impact-preview")]
    public Task<IActionResult> Impact(string id, AggregateConfigImpactCommandDto request, CancellationToken ct)
        => Run("preview", "CONFIG_REVISION", id, (s, c) => s.PreviewConfigAsync(c, request.Context, id, request.Impact, ct), ct);
    [HttpPost("configs/{id}/revisions")]
    public Task<IActionResult> Revision(string id, AggregateConfigRevisionCommandDto request, CancellationToken ct)
        => Run(request.CommandId, "CONFIG_REVISION", id, (s, c) => s.SaveConfigAsync(c, request.Context, id, request.Impact, request.ConfirmationToken, ct), ct);
    [HttpPost("instances/{id}/unlink-preview")]
    public Task<IActionResult> UnlinkPreview(string id, AggregateUnlinkPreviewCommandDto request, CancellationToken ct)
        => Run("preview", "UNLINK", id, (s, c) => s.PreviewUnlinkAsync(c, request.Context, id, request.ExpectedRevision, ct), ct);
    [HttpPost("instances/{id}/unlink")]
    public Task<IActionResult> Unlink(string id, AggregateUnlinkAllCommandDto request, CancellationToken ct)
        => Run(request.CommandId, "UNLINK", id, (s, c) => s.UnlinkAllAsync(c, request.Context, id, request.ExpectedRevision, request.ConfirmationToken, ct), ct);
    [HttpPut("instances/{id}/raw-draft")]
    public Task<IActionResult> Raw(string id, AggregateRawDraftCommandDto request, CancellationToken ct)
        => Run(request.CommandId, "RAW_DRAFT", id, (s, c) => s.SaveRawDraftAsync(c, request.Context, id, request.ExpectedRevision, request.RawJson, ct), ct);
    [HttpPost("data-window/preview")]
    public Task<IActionResult> DatePreview(AggregateDeclarationPreviewCommandDto request, CancellationToken ct)
        => Run("preview", "DATA_WINDOW", ContextIdentity(request.Context), (s, c) => s.PreviewDeclarationAsync(c, request.Context, request.Declaration, ct), ct);
    [HttpPost("data-window/apply")]
    public Task<IActionResult> DateApply(AggregateDeclarationSaveCommandDto request, CancellationToken ct)
        => Run(request.CommandId, "DATA_WINDOW", ContextIdentity(request.Context), (s, c) => s.SaveDeclarationAsync(c, request.Context, request.Declaration, request.ConfirmationToken, ct), ct);
    [HttpPost("instances/{id}/read")]
    public Task<IActionResult> Read(string id, AggregateInstanceReadCommandDto request, CancellationToken ct)
        => Run("read", "READ", id, (s, c) => s.ReadInstanceAsync(c, request.Context, id, ct), ct);

    [HttpPost("instances/{id}/computation/status")]
    public Task<IActionResult> ComputationStatus(string id, AggregateInstanceReadCommandDto request, CancellationToken ct)
        => Run("read-status", "READ", id, (s, c) => s.ReadComputationStatusAsync(c, request.Context, id, ct), ct);
    [HttpPost("configs/{id}/read")]
    public Task<IActionResult> ReadConfig(string id, AggregateConfigReadCommandDto request, CancellationToken ct)
        => Run("read", "READ", id, (s, c) => s.ReadConfigAsync(c, request.Context, id, ct, request.Revision), ct);

    private async Task<IActionResult> Run<T>(string commandId, string operation, string contextId,
        Func<AggregateCommandService, AggregateCommandContext, Task<T>> action, CancellationToken ct)
    {
        if (!AggregateIntegrationGate.IsV2(Request)) StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Run, "AGGREGATE_MAPPING_COMMAND");
        if (!(AggregateIntegrationGate.IsV2(Request) ? AggregateIntegrationGate.V2Ready(configuration) : AggregateIntegrationGate.Ready(configuration))) return Conflict(new { code = "AGG_ACTIVATION_REQUIRED" });
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var session = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(session)) return Unauthorized();
        var encodedKey = configuration["AggregateMapping:ConfirmationKeyBase64"];
        if (string.IsNullOrWhiteSpace(encodedKey)) return StatusCode(503, new { code = "AGG_CONFIRMATION_KEY_UNAVAILABLE" });
        byte[] key;
        try { key = Convert.FromBase64String(encodedKey); }
        catch (FormatException) { return StatusCode(503, new { code = "AGG_CONFIRMATION_KEY_UNAVAILABLE" }); }
        if (key.Length < 32) return StatusCode(503, new { code = "AGG_CONFIRMATION_KEY_UNAVAILABLE" });
        try
        {
            var store = new AggregateMongoStore(db, transactions, payloadReader, payloadWriter);
            var service = new AggregateCommandService(store, new AggregateMongoCommandReader(db, payloadReader, AggregateIntegrationGate.IsV2(Request) && AggregateIntegrationGate.V2Ready(configuration)), new(key));
            var value = await action(service, new(commandId, operation, contextId, actor, session, DateTimeOffset.UtcNow));
            var response = JsonSerializer.SerializeToNode(AggregateListTransport.Compact(value), AggregateCanonical.Json)!;
            if (AggregateIntegrationGate.IsV2(Request))
            {
                var scopes = new AggregateListReadScopes(db, payloadReader);
                if (value is AggregateSubmissionPreview submission && submission.Instances.Count > 0)
                    response["listReadId"] = await scopes.Issue(submission.Instances[0].Preview.Context, submission.Instances, actor, session, ct);
                else if (value is AggregateInstanceReadResult read)
                {
                    try
                    {
                        var row = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
                            .Find(new BsonDocument("_id", read.Instance.Id)).FirstOrDefaultAsync(ct)
                            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
                        var instance = AggregateMongoTransaction.Read<AggregateInstanceState>(row).Value;
                        if (instance.Revision != read.Instance.Revision) throw new AggregatePreviewException("AGG_INPUT_STALE");
                        if (instance.Applied != null && read.ResultReadError == null)
                            response["listReadId"] = await scopes.Issue(instance.Context, [instance.Applied], actor, session, ct,
                                new Dictionary<string, long> { [instance.Id] = instance.Revision });
                    }
                    catch (AggregatePreviewException ex) { response["listReadError"] = ex.Code; }
                }
            }
            Response.Headers.CacheControl = "no-store"; return Ok(response);
        }
        catch (AggregatePreviewException ex)
        {
            var status = ex.Code.Contains("UNAVAILABLE", StringComparison.Ordinal) ? 404 : ex.Code == "AGG_SOURCE_LOCKED" ? 423
                : ex.Code.Contains("FORBIDDEN", StringComparison.Ordinal) ? 403 : 409;
            return StatusCode(status, new AggregateErrorDto(ex.Code, HttpContext.TraceIdentifier, [new(ex.Code, ex.Path, "Command was not committed.")], false));
        }
    }
    private static AggregateMappingChange Change(AggregateMappingChangeDto value)
        => new(value.ExpectedRevision, value.Overrides, value.Selection, value.UnlinkedMembers, value.ResetToPinned);
    private static string ContextIdentity(AggregatePeriodContextDto context)
        => context.ReportId ?? context.BindingId + ":" + (context.Kind == "ONCE" ? "ONCE" : context.PeriodKey);
}
