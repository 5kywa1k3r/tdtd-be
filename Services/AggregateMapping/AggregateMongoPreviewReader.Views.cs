using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.AggregateMapping;

internal sealed partial class AggregateMongoPreviewReader
{
    internal async Task<(Work Work, WorkAssignment? Assignment, bool CanEdit, IReadOnlyList<string> RoleIds)> ViewOwnerAsync(
        string workId, string kind, string ownerId, string actor, CancellationToken ct)
    {
        Id(workId); Id(ownerId); Id(actor);
        if (kind is not ("WORK" or "ASSIGNMENT") || kind == "WORK" && ownerId != workId)
            throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var user = await db.Users.Find(x => x.Id == actor && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_AUTH_REQUIRED");
        var work = await db.Works.Find(x => x.Id == workId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        if (!string.IsNullOrEmpty(work.DynamicFlowRuntimeInstanceId)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        WorkAssignment? assignment = null;
        bool canRead, canEdit;
        var roles = await db.DocRoles.Find(x => x.DocType == DocType.WORK && x.DocId == workId && x.UserId == actor && !x.IsDeleted).ToListAsync(ct);
        if (kind == "WORK")
        {
            canRead = roles.Count > 0;
            canEdit = roles.Any(r => r.Role == DocRoleType.OWNER);
        }
        else
        {
            assignment = await db.WorkAssignments.Find(x => x.Id == ownerId && x.WorkId == workId && !x.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            if (DynamicFlowBranchVisibility.IsFlowAssignment(assignment)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            canRead = await WorkAssignmentReadAccessHelper.CanReadAssignmentOrAncestorAsync(db, assignment, actor, ct);
            canEdit = assignment.Assignees.Any(a => a.UserId == actor);
        }
        if (!canRead) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        canEdit &= !work.CompletedAtUtc.HasValue && work.Status != WorkStatus.S3;
        var ancestor = assignment;
        var seen = new HashSet<string>();
        while (ancestor != null)
        {
            if (!seen.Add(ancestor.Id)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            canEdit &= ancestor.IsActive && !ancestor.CompletedAtUtc.HasValue;
            ancestor = string.IsNullOrEmpty(ancestor.ParentAssignmentId) ? null
                : await db.WorkAssignments.Find(x => x.Id == ancestor.ParentAssignmentId && x.WorkId == workId && !x.IsDeleted).FirstOrDefaultAsync(ct)
                    ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        }
        return (work, assignment, canEdit, roles.Select(r => r.Id).ToArray());
    }

    internal async Task<AggregateReadContext> ReadViewContextAsync(AggregatePeriodContextDto selector, string actor, CancellationToken ct)
    {
        var owner = selector.View ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var scope = await ViewOwnerAsync(selector.WorkId, owner.Kind, owner.Id, actor, ct);
        var id = AggregateCanonical.Key("VIEW", owner.Kind, owner.Id);
        if (owner.ViewId != id || selector.ReportId != null || selector.WorkReportPeriodId != null || selector.PeriodInstanceKey != null
            || selector.AssignmentId != (owner.Kind == "WORK" ? "" : owner.Id)) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        var row = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Views).Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_VIEW_NOT_CONFIGURED");
        var view = AggregateMongoTransaction.Read<AggregateViewState>(row).Value;
        if (view.WorkId != selector.WorkId || view.OwnerKind != owner.Kind || view.OwnerId != owner.Id)
            throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        var generation = view.Current.BindingId == selector.BindingId ? view.Current : view.History.SingleOrDefault(h => h.BindingId == selector.BindingId)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        var editable = scope.CanEdit && generation == view.Current;
        var template = await Template(generation.Form, ct);
        var schema = await SchemaAsync(template, ct);
        var declaration = await dates.ReadAsync("VIEW", id, ct);
        var context = new AggregatePeriodContextDto("ONCE", view.WorkId, owner.Kind == "WORK" ? "" : owner.Id,
            generation.BindingId, null, null, null, null, declaration?.StartDate, declaration?.EndDate, null,
            AggregateDigest.Of(new { generation.BindingId, generation.Form, Parent = scope.Assignment?.ParentAssignmentId })) { View = owner };
        // These are target edit facts. Submit/review remain denied for a standalone view.
        var facts = new AggregateAuthorityFacts(true, true, true, true, true, false, editable, false, false,
            true, v2Enabled && editable, true, "Draft", false, true, editable, true, true);
        var previous = new Dictionary<string, AggregateValue>();
        string? savedResultReadError = null;
        if (generation.InstanceId is { } instanceId)
        {
            var saved = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id", instanceId)).FirstOrDefaultAsync(ct);
            var instance = saved == null ? null : AggregateMongoTransaction.Read<AggregateInstanceState>(saved).Value;
            if (instance?.Applied != null)
            {
                try { await ValidateViewSourcesAsync(instance.Applied, actor, context, schema, facts, ct); }
                catch (AggregatePreviewException ex) when (ex.Code == "AGG_SOURCE_UNAVAILABLE") { savedResultReadError = ex.Code; }
                var versionRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Versions).Find(new BsonDocument("_id", AggregateCommandService.VersionKey(instance.ConfigId, instance.ConfigRevision))).FirstOrDefaultAsync(ct)
                    ?? throw new AggregatePreviewException("AGG_CONFIG_UNAVAILABLE");
                var recipe = AggregateOverlay.Effective(AggregateMongoTransaction.Read<AggregateConfigVersion>(versionRow).Value, instance);
                foreach (var result in savedResultReadError == null ? instance.Applied.Preview.Results : [])
                    previous[recipe.Nodes.Single(n => n.Id == result.NodeId).Inputs.Single(p => p.Id == result.PortId).MemberId!] = new(result.ValueType, result.State == "RESULT" ? "VALUE" : result.State) { StoredWire = result.Value };
            }
        }
        var auth = AggregateDigest.Of(new { actor, scope.Work.CreatedByUserId, scope.RoleIds, scope.Assignment?.Assignees, scope.CanEdit, userUnit = scope.Work.Owner?.UnitId });
        return new(context, schema, facts, new(view.PayloadRevision, 0, 0, 0, schema.Pin.SchemaHash, ""), auth, declaration, previous) { SavedResultReadError = savedResultReadError };
    }

    private async Task ValidateViewSourcesAsync(AggregatePreviewEnvelope applied, string actor, AggregatePeriodContextDto context,
        AggregateSchema schema, AggregateAuthorityFacts facts, CancellationToken ct)
    {
        var read = new AggregateReadContext(context, schema, facts, new(0, 0, 0, 0, schema.Pin.SchemaHash, ""), "", null, new Dictionary<string, AggregateValue>());
        var found = new HashSet<string>();
        foreach (var form in applied.SourceValues.Select(s => s.Form).Distinct())
        {
            var listing = await ListSourcesAsync(read, form, actor, ct);
            foreach (var header in listing.Headers.Where(h => h.WholeReportReadable && h.Pin.Status == "Approved" && h.Pin.IsCurrent)) found.Add(header.Pin.ReportId);
        }
        if (applied.Preview.LinkedSources.Any(p => !found.Contains(p.ReportId))) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
    }
}
