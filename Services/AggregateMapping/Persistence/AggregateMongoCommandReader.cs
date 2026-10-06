using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed class AggregateMongoDataWindows(MongoDbContext db) : IAggregateBatchDataWindowReader
{
    public async Task<IReadOnlyDictionary<string, AggregateDataWindowDeclarationDto?>> ReadManyAsync(string kind, IReadOnlyList<string> identities, CancellationToken ct)
    {
        var result = identities.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => (AggregateDataWindowDeclarationDto?)null, StringComparer.Ordinal);
        foreach (var chunk in result.Keys.ToArray().Chunk(256))
        {
            var ids = chunk.Select(id => kind + ":" + id).ToArray();
            var rows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Declarations)
                .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids)))).ToListAsync(ct);
            foreach (var row in rows)
            {
                var value = AggregateMongoTransaction.Read<AggregateDeclarationState>(row).Value;
                var key = row["_id"].AsString[(kind.Length + 1)..];
                if (value.Kind != kind || value.Identity != key) throw new AggregatePreviewException("AGG_INPUT_STALE");
                result[key] = value.Declaration;
            }
        }
        return result;
    }
    public async Task<AggregateDataWindowDeclarationDto?> ReadAsync(string kind, string identity, CancellationToken ct)
    {
        var row = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Declarations).Find(new BsonDocument("_id", kind + ":" + identity)).FirstOrDefaultAsync(ct);
        return row == null ? null : AggregateMongoTransaction.Read<AggregateDeclarationState>(row).Value.Declaration;
    }
}

internal sealed partial class AggregateMongoCommandReader(MongoDbContext db, IWorkReportPayloadReader payloads, bool v2Enabled = false,
    Func<AggregatePreviewProgress, Task>? progress = null, bool metadataOnly = false) : IAggregateCommandReader
{
    public Task<AggregateCommitAuthority> AuthorizeStatusAsync(AggregatePeriodContextDto context, string actor, string sessionKey, CancellationToken ct)
        => new AggregateMongoCommandReader(db, payloads, v2Enabled, metadataOnly: true).AuthorizeAsync(context, actor, sessionKey, ct);
    public async Task<AggregateCommitAuthority> AuthorizeAsync(AggregatePeriodContextDto context, string actor, string sessionKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) throw new AggregatePreviewException("AGG_AUTH_REQUIRED");
        if (context.View != null) return await AuthorizeViewAsync(context, actor, sessionKey, ct);
        if (context.ReportId == null) return await AuthorizeSlotAsync(context, actor, sessionKey, ct);
        var read = await new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), v2Enabled, metadataOnly).ReadContextAsync(context, actor, ct);
        var lockRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks).Find(new BsonDocument("_id", "REPORT:" + context.ReportId)).FirstOrDefaultAsync(ct);
        var locked = lockRow != null && AggregateMongoTransaction.Read<AggregateLockState>(lockRow).Value.Owners.Count > 0;
        read = read with { Authority = read.Authority with { TargetLockedByConsumer = locked } };
        var pins = new List<AggregateAuthorityPin>();
        await Pin(pins, db.Users.CollectionNamespace.CollectionName, actor, ct);
        await Pin(pins, db.Works.CollectionNamespace.CollectionName, context.WorkId, ct);
        await Pin(pins, db.WorkAssignments.CollectionNamespace.CollectionName, context.AssignmentId, ct);
        var scopeAssignment = await db.WorkAssignments.Find(a => a.Id == context.AssignmentId).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        foreach (var ancestor in await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.ReadAncestorsAsync(db, scopeAssignment, ct))
            await Pin(pins, db.WorkAssignments.CollectionNamespace.CollectionName, ancestor.Id, ct);
        await Pin(pins, db.WorkTemplateAssignees.CollectionNamespace.CollectionName, context.BindingId, ct);
        await Pin(pins, db.WorkAssignmentReports.CollectionNamespace.CollectionName, context.ReportId!, ct);
        await Pin(pins, db.DynamicFormTemplates.CollectionNamespace.CollectionName, read.TargetSchema.Pin.FormId, ct);
        await PinBoundCatalogs(pins, read.TargetSchema.Pin.FormId, ct);
        if (context.WorkReportPeriodId != null) await Pin(pins, db.WorkReportPeriods.CollectionNamespace.CollectionName, context.WorkReportPeriodId, ct);
        await PinDeclaration(pins, "REPORT:" + context.ReportId, ct);
        return new(actor, sessionKey, read, pins);
    }
    public async Task<AggregatePreviewEnvelope> PreviewAsync(AggregateInstanceState instance, AggregateRecipeDto recipe,
        AggregateCommitAuthority authority, CancellationToken ct)
    {
        var inner = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), v2Enabled);
        var expected = authority.Read.Revisions with { InstanceRevision = instance.Revision, ConfigRevision = instance.ConfigRevision };
        var read = authority.Read with { Revisions = expected };
        var preview = await new AggregatePreviewService(new PinnedReader(inner, read), new AggregateContentTableStore(db), new AggregateListStore(db)).PreviewAsync(
            new(read.Context, instance.Id, instance.ConfigId, expected, recipe, instance.Selection), authority.Actor, ct, progress);
        if (authority.Pins is not List<AggregateAuthorityPin> pins) throw new InvalidOperationException("AGG_AUTHORITY_CAPTURE_REQUIRED");
        var requested = new List<(string Collection, string Id)>();
        foreach (var source in preview.Preview.LinkedSources)
        {
            requested.Add((db.WorkAssignments.CollectionNamespace.CollectionName, source.AssignmentId));
            requested.Add((db.WorkTemplateAssignees.CollectionNamespace.CollectionName, source.BindingId));
            requested.Add((db.WorkAssignmentReports.CollectionNamespace.CollectionName, source.ReportId));
            if (source.WorkReportPeriodId != null) requested.Add((db.WorkReportPeriods.CollectionNamespace.CollectionName, source.WorkReportPeriodId));
            requested.Add((AggregateCollections.Declarations, "REPORT:" + source.ReportId));
        }
        foreach (var source in recipe.Nodes.Where(n => n.Kind == "SOURCE"))
        {
            var selection = instance.Selection.Sources.Single(s => s.SourceNodeId == source.Id);
            var explicitIds = selection.ReportIds.Concat(selection.ExcludedReportIds).ToArray();
            foreach (var hidden in inner.CapturedInactiveSources.Where(h => explicitIds.Contains(h.Pin.ReportId)))
            {
                requested.Add((db.WorkAssignments.CollectionNamespace.CollectionName, hidden.Pin.AssignmentId));
                requested.Add((db.WorkTemplateAssignees.CollectionNamespace.CollectionName, hidden.Pin.BindingId));
                requested.Add((db.WorkAssignmentReports.CollectionNamespace.CollectionName, hidden.Pin.ReportId));
            }
            requested.Add((db.DynamicFormTemplates.CollectionNamespace.CollectionName, source.Form!.FormId));
            await PinBoundCatalogs(pins, source.Form.FormId, ct);
        }
        foreach (var slot in preview.Preview.Coverage)
        {
            requested.Add((db.WorkTemplateAssignees.CollectionNamespace.CollectionName, slot.BindingId));
            requested.Add((AggregateCollections.Declarations, "SLOT:" + slot.SlotKey));
        }
        await PinMany(pins, requested, ct);
        if (!await inner.IsCurrentAsync(authority.Read, preview.Preview.LinkedSources, "", authority.Actor, ct))
            throw new AggregatePreviewException("AGG_INPUT_STALE");
        return preview;
    }
    private async Task PinDeclaration(List<AggregateAuthorityPin> pins, string id, CancellationToken ct)
    {
        var row = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Declarations).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        if (row == null) return;
        var pin = new AggregateAuthorityPin(AggregateCollections.Declarations, id, Fingerprint(row));
        var old = pins.SingleOrDefault(p => p.Collection == pin.Collection && p.Id == id);
        if (old != null && old != pin) throw new AggregatePreviewException("AGG_INPUT_STALE");
        if (old == null) pins.Add(pin);
    }
    private async Task PinBoundCatalogs(List<AggregateAuthorityPin> pins, string formId, CancellationToken ct)
    {
        var form = await db.DynamicFormTemplates.Find(f => f.Id == formId && !f.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var fields = JsonSerializer.Deserialize<List<tdtd_be.DTOs.DynamicForms.DynamicFormFieldDto>>(form.FieldsJson, AggregateCanonical.Json) ?? [];
        foreach (var id in fields.Where(f => f.ValueSource?.SourceType == "ENUM_CATALOG").Select(f => f.ValueSource!.CatalogId!)
            .Concat(AggregateNativePayloadAdapter.ListCatalogIds(form)).Distinct(StringComparer.Ordinal))
            await Pin(pins, db.LabelEnumCatalogs.CollectionNamespace.CollectionName, id, ct);
    }
    private async Task Pin(List<AggregateAuthorityPin> pins, string collection, string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var row = await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var value = new AggregateAuthorityPin(collection, id, Fingerprint(row));
        var existing = pins.SingleOrDefault(p => p.Collection == collection && p.Id == id);
        if (existing != null && existing != value) throw new AggregatePreviewException("AGG_INPUT_STALE");
        if (existing == null) pins.Add(value);
    }
    internal static string Fingerprint(BsonDocument row)
    {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("aggregateCommitFence");
        // Reservations and projection/lease bookkeeping do not change the reviewed data.
        foreach (var name in copy.Names.Where(n => n.StartsWith("payloadMutation", StringComparison.Ordinal)
            || n.StartsWith("lifecycleProjection", StringComparison.Ordinal)
            || n.StartsWith("reportLifecycleLease", StringComparison.Ordinal)).ToArray()) copy.Remove(name);
        using var json = JsonDocument.Parse(copy.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson }));
        return AggregateCanonical.Hash(json.RootElement);
    }
    internal sealed class PinnedReader(AggregateMongoPreviewReader inner, AggregateReadContext captured) : IAggregatePreviewReader, IAggregateBatchPreviewReader, IAggregateListTargetValidator
    {
        public Task ValidateListResultAsync(AggregateReadContext context, string memberId, AggregateListValue value, CancellationToken ct)
            => inner.ValidateListResultAsync(context, memberId, value, ct);
        public Task PreparePayloadBatchAsync(AggregateReadContext context, IReadOnlyList<AggregateSourceHeader> headers, string actor, CancellationToken ct)
            => inner.PreparePayloadBatchAsync(context, headers, actor, ct);
        public Task<AggregateReadContext> ReadContextAsync(AggregatePeriodContextDto selector, string actor, CancellationToken ct) => Task.FromResult(captured);
        public Task<AggregateSchema> ReadSchemaAsync(AggregateFormPinDto pin, AggregateReadContext context, string actor, CancellationToken ct) => inner.ReadSchemaAsync(pin, context, actor, ct);
        public Task<AggregateSourceListing> ListSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct) => inner.ListSourcesAsync(context, form, actor, ct);
        public Task<AggregateSourceListing> ListReportSetSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct) => inner.ListReportSetSourcesAsync(context, form, actor, ct);
        public Task<IReadOnlyList<AggregateSourceHeader>> ReadInactiveSourcesAsync(AggregateReadContext context, AggregateFormPinDto form,
            IReadOnlyList<string> ids, string actor, CancellationToken ct) => inner.ReadInactiveSourcesAsync(context, form, ids, actor, ct);
        public Task<AggregatePayload> ReadPayloadAsync(AggregateReadContext context, AggregateSourceHeader header, AggregateSchema schema, string actor, CancellationToken ct) => inner.ReadPayloadAsync(context, header, schema, actor, ct);
        public Task<bool> IsCurrentAsync(AggregateReadContext context, IReadOnlyList<AggregateSourcePinDto> pins, string membershipRevision, string actor, CancellationToken ct)
            => inner.IsCurrentAsync(context with { Revisions = context.Revisions with { InstanceRevision = 0, ConfigRevision = 0 } }, pins, membershipRevision, actor, ct);
    }
}
