using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed partial class AggregateMongoCommandReader
{
    public Task ValidateSavedResultAsync(AggregateCommitAuthority authority, AggregatePreviewEnvelope applied, CancellationToken ct)
        => new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), v2Enabled)
            .ValidateSavedSourcesAsync(authority.Read, applied, authority.Actor, ct);
    // Source headers contain current-version/payload hashes, ACL and declared windows.
    // Revalidate those headers and schemas without loading text/cells or evaluating.
    public async Task<string> InputStampAsync(AggregateInstanceState instance, AggregateRecipeDto recipe,
        AggregateCommitAuthority authority, CancellationToken ct)
    {
        var inner = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), v2Enabled);
        var inputs = new List<object>();
        var pins = authority.Pins as List<AggregateAuthorityPin> ?? throw new InvalidOperationException("AGG_AUTHORITY_CAPTURE_REQUIRED");
        var requested = new List<(string Collection, string Id)>();
        foreach (var form in recipe.Nodes.Where(n => n.Kind == "SOURCE").Select(n => n.Form!).Distinct()
            .OrderBy(f => f.FormId, StringComparer.Ordinal))
        {
            var schema = await inner.ReadSchemaAsync(form, authority.Read, authority.Actor, ct);
            var reportSet = recipe.Nodes.Where(n => n.Kind == "SOURCE" && n.Form == form).SelectMany(n => n.Outputs)
                .All(p => recipe.TimeRules.Single(r => r.Id == p.TimeRuleId).Mode == AggregateReportSetFilter.Mode);
            var listing = reportSet ? await inner.ListReportSetSourcesAsync(authority.Read, form, authority.Actor, ct)
                : await inner.ListSourcesAsync(authority.Read, form, authority.Actor, ct);
            var nodeIds = recipe.Nodes.Where(n => n.Kind == "SOURCE" && n.Form == form).Select(n => n.Id).ToHashSet();
            var present = listing.Headers.Select(h => h.Pin.ReportId).ToHashSet();
            var absentIds = instance.Selection.Sources.Where(s => nodeIds.Contains(s.SourceNodeId))
                .SelectMany(s => s.ReportIds.Concat(s.ExcludedReportIds)).Where(id => !present.Contains(id)).Distinct().ToArray();
            var inactive = absentIds.Length == 0 ? [] : await inner.ReadInactiveSourcesAsync(authority.Read, form, absentIds, authority.Actor, ct);
            if (absentIds.Any(id => !inactive.Any(h => h.Pin.ReportId == id))) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            // Mongo enumeration order is not source identity. Stable metadata
            // stamps must hash the same membership identically in every query.
            listing = listing with { Headers = listing.Headers.OrderBy(h => h.Pin.ReportId, StringComparer.Ordinal).ToArray(),
                Slots = listing.Slots.OrderBy(s => s.Key, StringComparer.Ordinal).ToArray() };
            if (!listing.Complete || listing.Headers.Any(h => !h.WholeReportReadable) || listing.Slots.Any(s => !s.Readable))
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            inputs.Add(new { schema, listing, inactive });
            requested.Add((db.DynamicFormTemplates.CollectionNamespace.CollectionName, form.FormId));
            await PinBoundCatalogs(pins, form.FormId, ct);
            foreach (var header in listing.Headers.Concat(inactive))
            {
                var source = header.Pin;
                requested.Add((db.WorkAssignments.CollectionNamespace.CollectionName, source.AssignmentId));
                requested.Add((db.WorkTemplateAssignees.CollectionNamespace.CollectionName, source.BindingId));
                requested.Add((db.WorkAssignmentReports.CollectionNamespace.CollectionName, source.ReportId));
                if (source.WorkReportPeriodId != null) requested.Add((db.WorkReportPeriods.CollectionNamespace.CollectionName, source.WorkReportPeriodId));
                requested.Add((AggregateCollections.Declarations, "REPORT:" + source.ReportId));
            }
            foreach (var slot in listing.Slots)
            {
                requested.Add((db.WorkTemplateAssignees.CollectionNamespace.CollectionName, slot.BindingId));
                requested.Add((AggregateCollections.Declarations, "SLOT:" + slot.Key));
            }
        }
        await PinMany(pins, requested, ct);
        return AggregateCanonical.Hash(new { recipe, instance.Selection, authority.Read.DataWindow,
            authority.Read.TargetSchema, inputs, dateFilters = AggregatePartialDate.FilterSemantics });
    }
}
