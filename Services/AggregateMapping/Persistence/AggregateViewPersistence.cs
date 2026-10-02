using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateViewPayload
{
    internal static JsonObject EmptyTables(DynamicFormTemplate form)
    {
        if (form.NativeTablesVersion == null) return new JsonObject();
        var tables = new JsonArray();
        foreach (var table in DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson) ?? [])
        {
            var rows = new JsonArray();
            if (table.Layout == "matrix") foreach (var row in table.Rows!)
                rows.Add(new JsonObject { ["rowId"] = row.Id, ["cells"] = new JsonObject() });
            tables.Add(new JsonObject { ["tableId"] = table.Id, [table.Layout == "matrix" ? "rows" : "records"] = rows });
        }
        return new JsonObject { ["nativeTables"] = new JsonObject { ["version"] = 1, ["schemaHash"] = form.PublishedSchemaHash, ["tables"] = tables } };
    }
}

internal sealed partial class AggregateMongoCommandReader
{
    private async Task<AggregateCommitAuthority> AuthorizeViewAsync(AggregatePeriodContextDto context, string actor, string sessionKey, CancellationToken ct)
    {
        var adapter = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), v2Enabled);
        var read = await adapter.ReadViewContextAsync(context, actor, ct);
        var scope = await adapter.ViewOwnerAsync(context.WorkId, context.View!.Kind, context.View.Id, actor, ct);
        var pins = new List<AggregateAuthorityPin>();
        await Pin(pins, db.Users.CollectionNamespace.CollectionName, actor, ct);
        await Pin(pins, db.Works.CollectionNamespace.CollectionName, context.WorkId, ct);
        foreach (var role in scope.RoleIds) await Pin(pins, db.DocRoles.CollectionNamespace.CollectionName, role, ct);
        var ancestor = scope.Assignment;
        var seen = new HashSet<string>();
        while (ancestor != null)
        {
            if (!seen.Add(ancestor.Id)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            await Pin(pins, db.WorkAssignments.CollectionNamespace.CollectionName, ancestor.Id, ct);
            ancestor = string.IsNullOrEmpty(ancestor.ParentAssignmentId) ? null : await db.WorkAssignments.Find(x => x.Id == ancestor.ParentAssignmentId && !x.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        }
        await Pin(pins, db.DynamicFormTemplates.CollectionNamespace.CollectionName, read.TargetSchema.Pin.FormId, ct);
        await PinBoundCatalogs(pins, read.TargetSchema.Pin.FormId, ct);
        await PinDeclaration(pins, "VIEW:" + context.View.ViewId, ct);
        var row = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Views).Find(new BsonDocument("_id", context.View.ViewId)).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        if (AggregateMongoTransaction.Read<AggregateViewState>(row).Value.PayloadRevision != read.Revisions.PayloadRevision)
            throw new AggregatePreviewException("AGG_INPUT_STALE");
        pins.Add(new(AggregateCollections.Views, context.View.ViewId, Fingerprint(row)));
        return new(actor, sessionKey, read, pins);
    }
}

internal sealed partial class AggregateMongoTransaction
{
    private async Task<long> WriteViewAsync(AggregateCommitAuthority authority, AggregateTargetWrite write, CancellationToken ct)
    {
        AggregateCommandService.Allow(AggregateAction.ApplyDraft, authority);
        AggregateCommandService.ValidPreview(write.Preview);
        var context = authority.Read.Context;
        await AggregateContentScopeGate.Fence(db, session, context.View!.ViewId, ct);
        if (write.Instance.Context != context) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        var stored = await GetAsync<AggregateViewState>(AggregateCollections.Views, context.View!.ViewId, ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var view = stored.Value;
        if (view.Current.BindingId != context.BindingId || view.Current.Form != authority.Read.TargetSchema.Pin
            || view.PayloadRevision != authority.Read.Revisions.PayloadRevision) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        var form = await db.DynamicFormTemplates.Find(session, f => f.Id == view.Current.Form.FormId && !f.IsDeleted && f.IsPublished).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        if (AggregateNativePayloadAdapter.Schema(form).Pin != view.Current.Form) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        // Shared canonicalizer and native validator, without a synthetic report/save path.
        _ = await AggregateTargetPayload.ValidateAsync(db, session, form, view.Id, write, "{}", AggregateViewPayload.EmptyTables(form).ToJsonString(), null, ct);
        var next = view with { PayloadRevision = checked(view.PayloadRevision + 1), Current = view.Current with { InstanceId = write.Instance.Id } };
        foreach (var result in write.Preview.Preview.Results)
        {
            if (result.Value is not { ValueKind: System.Text.Json.JsonValueKind.Object } value || !value.TryGetProperty("kind", out var kind) || kind.GetString() != AggregateContentTableStore.Kind) continue;
            var reference = System.Text.Json.JsonSerializer.Deserialize<AggregateContentReference>(value, AggregateCanonical.Json)!;
            var member = write.Recipe.Nodes.Single(n => n.Id == result.NodeId).Inputs.Single(p => p.Id == result.PortId).MemberId!;
            var key = AggregateCanonical.Key(view.Id, next.PayloadRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), member);
            await PutAsync(AggregateCollections.ContentSnapshots, key, 0, new AggregateContentSnapshotIntent("", member, next.PayloadRevision, reference, view.Id), view.WorkId, view.Id, [], ct);
        }
        await PutAsync(AggregateCollections.Views, view.Id, stored.Version, next, view.WorkId, view.OwnerId, [], ct);
        await AggregateContentRetentionJob.PlanOnSave(db, session, view.Id, view.WorkId, ct);
        await AggregatePreviewInvalidation.CancelSuperseded(db, session, view.WorkId, ct, target: view.Id);
        return next.PayloadRevision;
    }
}

