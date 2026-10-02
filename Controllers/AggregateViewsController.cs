using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

public sealed record AggregateViewOwnerRequest(string WorkId, string OwnerKind, string OwnerId, string? HistoryBindingId = null);
public sealed record AggregateViewBindRequest(AggregateViewOwnerRequest Owner, string FormId, long ExpectedRevision,
    string? ConfirmationToken = null);

[ApiController, Authorize, Route("api/aggregate-v2/views"), RequestSizeLimit(16384)]
public sealed class AggregateViewsController(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner transactions,
    IWorkReportPayloadReader payloads, IWorkReportPayloadWriter writer, IConfiguration config) : ControllerBase
{
    [HttpPost("read")]
    public Task<IActionResult> Read(AggregateViewOwnerRequest owner, CancellationToken ct) => Run(async (actor, session) =>
    {
        var adapter = Adapter();
        var scope = await adapter.ViewOwnerAsync(owner.WorkId, owner.OwnerKind, owner.OwnerId, actor, ct);
        var row = await Row(owner, ct);
        var view = row == null ? null : AggregateMongoTransaction.Read<AggregateViewState>(row).Value;
        var forms = scope.CanEdit ? await db.DynamicFormTemplates.Find(f => f.CreatedByUserId == actor && f.IsPublished && f.IsActive && !f.IsDeleted)
            .SortByDescending(f => f.UpdatedAtUtc).Limit(201).Project(f => new { f.Id, f.Name, f.VersionNo }).ToListAsync(ct) : null;
        var selected = view == null || owner.HistoryBindingId == null ? view?.Current : view.History.SingleOrDefault(h => h.BindingId == owner.HistoryBindingId)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        AggregateReadContext? context = view == null ? null : await adapter.ReadViewContextAsync(Selector(view) with { BindingId = selected!.BindingId }, actor, ct);
        var authority = context == null ? null : await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(context.Context, actor, session, ct);
        AggregateInstanceReadResult? result = null;
        string? readId = null;
        if (context != null && selected?.InstanceId is { } instanceId)
        {
            var service = new AggregateCommandService(Store(), new AggregateMongoCommandReader(db, payloads, true), Tokens());
            result = await service.ReadInstanceAsync(new("read", "READ", view!.Id, actor, session, DateTimeOffset.UtcNow), context.Context, instanceId, ct);
            var instanceRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id", instanceId)).FirstOrDefaultAsync(ct);
            var instance = instanceRow == null ? null : AggregateMongoTransaction.Read<AggregateInstanceState>(instanceRow).Value;
            if (instance?.Applied != null && result.ResultReadError == null)
                readId = await new AggregateListReadScopes(db, payloads).Issue(context.Context, [instance.Applied], actor, session, ct, new Dictionary<string, long> { [instanceId] = instance.Revision });
            // Retry durable snapshot intents after an authorized read, including worker/config refreshes.
            try { await AggregateContentSnapshotJob.Schedule(db, HttpContext.RequestServices?.GetService<Hangfire.IBackgroundJobClient>(), view!.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* Pending intents remain retryable. */ }
        }
        return new { Revision = row?["version"].ToInt64() ?? 0, CanEdit = scope.CanEdit && owner.HistoryBindingId == null, Context = context?.Context,
            Target = context == null ? null : await adapter.EditorSchemaAsync(context, context.TargetSchema.Pin, actor, ct),
            Forms = forms?.Take(200), HasMoreForms = forms?.Count > 200, View = view, Result = AggregateListTransport.Compact(result), ListReadId = readId };
    }, ct);

    [HttpPost("binding/preview")]
    public Task<IActionResult> Preview(AggregateViewBindRequest request, CancellationToken ct) => Run(async (actor, session) =>
    {
        var prepared = await Prepare(request, actor, ct);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var token = Tokens().Issue(Confirmation(request, actor, session, prepared.Evidence, expiry));
        return new { ConfirmationToken = token, ExpiresAtUtc = expiry, Previous = prepared.Previous?.Current.Form,
            Next = prepared.Pin, RetainedPreviousVersions = Math.Min(3, (prepared.Previous?.History.Count ?? -1) + 1),
            Discarded = prepared.Previous?.History.Count >= 3 ? prepared.Previous.History[0].Form : null,
            Warning = "Đổi biểu mẫu sẽ mở cấu hình tổng hợp mới. Kết quả cũ được giữ trong lịch sử, tối đa ba bản cũ; bản cũ nhất vượt giới hạn sẽ bị bỏ. Báo cáo đã nộp và biểu mẫu gốc không bị sửa." };
    }, ct);

    [HttpPost("binding/apply")]
    public Task<IActionResult> Bind(AggregateViewBindRequest request, CancellationToken ct) => Run(async (actor, session) =>
    {
        var prepared = await Prepare(request, actor, ct);
        Tokens().Verify(request.ConfirmationToken ?? "", Confirmation(request, actor, session, prepared.Evidence, DateTimeOffset.UtcNow.AddMinutes(5)), DateTimeOffset.UtcNow);
        var id = Id(request.Owner);
        var next = new AggregateViewState(id, request.Owner.WorkId, request.Owner.OwnerKind, request.Owner.OwnerId,
            new(Guid.NewGuid().ToString("N"), prepared.Pin, null, DateTimeOffset.UtcNow),
            prepared.Previous == null ? [] : prepared.Previous.History.Concat([prepared.Previous.Current]).TakeLast(3).ToArray(),
            checked((prepared.Previous?.PayloadRevision ?? 0) + 1));
        await Store().ExecuteAsync(async (tx, token) =>
        {
            // Re-read ownership, Form and head before CAS. All authority rows participate
            // in the transaction so revoke/rebind cannot race a successful write.
            var pins = new List<AggregateAuthorityPin>();
            async Task Pin(string collection, string key) {
                var document = await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", ObjectId.Parse(key))).FirstOrDefaultAsync(token)
                    ?? throw new AggregatePreviewException("AGG_INPUT_STALE");
                pins.Add(new(collection, key, AggregateMongoCommandReader.Fingerprint(document)));
            }
            await Pin(db.Users.CollectionNamespace.CollectionName, actor);
            await Pin(db.Works.CollectionNamespace.CollectionName, request.Owner.WorkId);
            await Pin(db.DynamicFormTemplates.CollectionNamespace.CollectionName, prepared.Pin.FormId);
            if (request.Owner.OwnerKind == "ASSIGNMENT")
            {
                var assignmentId = request.Owner.OwnerId;
                var seen = new HashSet<string>();
                while (!string.IsNullOrEmpty(assignmentId))
                {
                    if (!seen.Add(assignmentId)) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
                    await Pin(db.WorkAssignments.CollectionNamespace.CollectionName, assignmentId);
                    var branch = await db.WorkAssignments.Find(a => a.Id == assignmentId && a.WorkId == request.Owner.WorkId && !a.IsDeleted).FirstOrDefaultAsync(token)
                        ?? throw new AggregatePreviewException("AGG_CONTEXT_STALE");
                    assignmentId = branch.ParentAssignmentId;
                }
            }
            foreach (var role in prepared.RoleIds) await Pin(db.DocRoles.CollectionNamespace.CollectionName, role);
            // Recheck after captures, before fencing those exact captures.
            var current = await Prepare(request, actor, token);
            if (prepared.Evidence != current.Evidence) throw new AggregatePreviewException("AGG_INPUT_STALE");
            var facts = new AggregateAuthorityFacts(true,true,true,true,true,false,true,false,false,true,true,true,"Draft",false,true,true,true,true);
            await tx.FenceAsync(new(actor, session, new(Selector(next), new(prepared.Pin, new Dictionary<string,AggregateMember>()), facts,
                new(0,0,0,0,prepared.Pin.SchemaHash,""), "", null, new Dictionary<string,AggregateValue>()), pins), [], [], token);
            if (prepared.Previous is { } previous)
            {
                foreach (var instance in await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(previous.WorkId, previous.Id), token))
                {
                    await tx.PutAsync(AggregateCollections.Instances, instance.Value.Id, instance.Version,
                        instance.Value with { State = "ARCHIVED", Generation = checked(instance.Value.Generation + 1) }, previous.WorkId, previous.Id, [], token);
                    var dependency = await tx.GetAsync<AggregateDependencyState>(AggregateCollections.Dependencies, instance.Value.Id, token);
                    if (dependency != null) await tx.DeleteAsync(AggregateCollections.Dependencies, instance.Value.Id, dependency.Version, token);
                }
                foreach (var claim in await tx.QueryAsync<AggregateTargetClaim>(AggregateCollections.Targets, new(previous.WorkId, previous.Id), token))
                    await tx.DeleteAsync(AggregateCollections.Targets, AggregateCanonical.Key(claim.Value.ReportId, claim.Value.MemberId), claim.Version, token);
                // Evict only this owner's expired generation; no report/Form/version deletion.
                foreach (var old in previous.History.Where(h => !next.History.Contains(h)))
                    if (old.InstanceId is { } oldId && await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, oldId, token) is { } oldInstance)
                        await tx.DeleteAsync(AggregateCollections.Instances, oldId, oldInstance.Version, token);
            }
            await tx.PutAsync(AggregateCollections.Views, id, request.ExpectedRevision, next, next.WorkId, next.OwnerId, [], token);
            return true;
        }, ct);
        return new { Context = Selector(next), Revision = request.ExpectedRevision + 1 };
    }, ct);

    private async Task<(AggregateViewState? Previous, AggregateFormPinDto Pin, string Evidence, IReadOnlyList<string> RoleIds)> Prepare(AggregateViewBindRequest request, string actor, CancellationToken ct)
    {
        if (request.Owner.HistoryBindingId != null) throw new AggregatePreviewException("AGG_CONFIG_EDIT_FORBIDDEN");
        var scope = await Adapter().ViewOwnerAsync(request.Owner.WorkId, request.Owner.OwnerKind, request.Owner.OwnerId, actor, ct);
        if (!scope.CanEdit) throw new AggregatePreviewException("AGG_CONFIG_EDIT_FORBIDDEN");
        var row = await Row(request.Owner, ct);
        if ((row?["version"].ToInt64() ?? 0) != request.ExpectedRevision) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        if (!ObjectId.TryParse(request.FormId, out _)) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        // Self-designed means owned published Form. A general view grant cannot bind it.
        var form = await db.DynamicFormTemplates.Find(f => f.Id == request.FormId && f.CreatedByUserId == actor && f.IsPublished && f.IsActive && !f.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_FORM_BIND_FORBIDDEN");
        var pin = AggregateNativePayloadAdapter.Schema(form).Pin;
        var previous = row == null ? null : AggregateMongoTransaction.Read<AggregateViewState>(row).Value;
        if (previous?.Current.Form == pin) throw new AggregatePreviewException("AGG_VIEW_FORM_UNCHANGED");
        return (previous, pin, AggregateCanonical.Hash(new { request.Owner, request.ExpectedRevision, pin, previous, scope.Work, scope.Assignment, scope.RoleIds }), scope.RoleIds);
    }
    private AggregateConfirmation Confirmation(AggregateViewBindRequest r, string actor, string session, string evidence, DateTimeOffset expiry)
        => new("VIEW_BIND", actor, session, Id(r.Owner), AggregateCanonical.Hash(new { r.Owner, r.FormId, r.ExpectedRevision }), evidence, expiry);
    private static string Id(AggregateViewOwnerRequest owner) => AggregateCanonical.Key("VIEW", owner.OwnerKind, owner.OwnerId);
    private Task<BsonDocument?> Row(AggregateViewOwnerRequest owner, CancellationToken ct)
        => db.Db.GetCollection<BsonDocument>(AggregateCollections.Views).Find(new BsonDocument("_id", Id(owner))).FirstOrDefaultAsync(ct)!;
    internal static AggregatePeriodContextDto Selector(AggregateViewState view) => new("ONCE", view.WorkId,
        view.OwnerKind == "WORK" ? "" : view.OwnerId, view.Current.BindingId, null, null, null, null, null, null, null, "")
        { View = new(view.OwnerKind, view.OwnerId, view.Id) };
    private AggregateMongoPreviewReader Adapter() => new(db, payloads, new AggregateMongoDataWindows(db), true);
    private AggregateMongoStore Store() => new(db, transactions, payloads, writer);
    private AggregateConfirmationTokens Tokens() => AggregateHostIntegration.Tokens(config);
    private async Task<IActionResult> Run<T>(Func<string, string, Task<T>> action, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AggregateIntegrationGate.V2Ready(config)) return Conflict(new { code = "AGG_ACTIVATION_REQUIRED" });
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var session = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(session)) return Unauthorized();
        try { return Ok(await action(actor, session)); }
        catch (AggregatePreviewException ex) { return StatusCode(ex.Code.Contains("FORBIDDEN") ? 403 : 409, new { code = ex.Code }); }
    }
}
