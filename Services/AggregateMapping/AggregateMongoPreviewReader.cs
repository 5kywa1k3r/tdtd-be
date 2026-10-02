using MongoDB.Bson;
using System.Text.Json;
using tdtd_be.Services.AggregateMapping.Persistence;
using MongoDB.Driver;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.AggregateMapping;

// P03 supplies declarations and config/instance persistence. No fabricated data dates.
internal interface IAggregateDataWindowReader
{
    Task<AggregateDataWindowDeclarationDto?> ReadAsync(string kind, string identity, CancellationToken ct);
}
internal sealed class AggregateUnstoredDataWindows : IAggregateDataWindowReader
{
    public Task<AggregateDataWindowDeclarationDto?> ReadAsync(string kind, string identity, CancellationToken ct)
        => Task.FromResult<AggregateDataWindowDeclarationDto?>(null);
}

internal interface IAggregateBatchDataWindowReader : IAggregateDataWindowReader
{
    Task<IReadOnlyDictionary<string, AggregateDataWindowDeclarationDto?>> ReadManyAsync(string kind,
        IReadOnlyList<string> identities, CancellationToken ct);
}

// Read-only repository adapter. Never call report.GetById, projection refresh or a writer.
internal sealed partial class AggregateMongoPreviewReader(MongoDbContext db, IWorkReportPayloadReader payloadReader,
    IAggregateDataWindowReader dates, bool v2Enabled = false) : IAggregatePreviewReader, IAggregateBatchPreviewReader, IAggregateListTargetValidator
{
    public async Task ValidateListResultAsync(AggregateReadContext context, string memberId, AggregateListValue value, CancellationToken ct)
    {
        var template = await Template(context.TargetSchema.Pin, ct);
        _ = await SchemaAsync(template, ct);
        var definition = tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson)!
            .Single(t => t.Id == memberId);
        System.Text.Json.Nodes.JsonObject tables;
        if (context.Context.View != null) tables = AggregateViewPayload.EmptyTables(template);
        else {
            var report = await db.WorkAssignmentReports.Find(r => r.Id == context.Context.ReportId && !r.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var payload = await payloadReader.LoadReportPayloadAsync(report, ct);
            WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
            tables = System.Text.Json.Nodes.JsonNode.Parse(payload.TableValuesJson!)!.AsObject();
        }
        var items = tables["nativeTables"]!["tables"]!.AsArray();
        var index = items.ToList().FindIndex(t => t!["tableId"]!.GetValue<string>() == memberId);
        if (index < 0) throw new AggregatePreviewException("AGG_LIST_TARGET_SCHEMA");
        items[index] = new System.Text.Json.Nodes.JsonObject { ["tableId"] = memberId,
            ["records"] = AggregateNativePayloadAdapter.ListRecords(definition, value, row => AggregateListWire.OutputId("preview", memberId, row.Origin)) };
        tdtd_be.Services.WorkAssignmentReports.Runtime.DynamicFormNativeTableValues.Validate(template, context.TargetSchema.Pin.SchemaHash,
            tables.ToJsonString(), submitting: false, optionSets: _listOptions);
    }
    private static readonly ProjectionDefinition<WorkAssignmentReport> HeaderOnly = Builders<WorkAssignmentReport>.Projection
        .Exclude(r => r.Values1DJson).Exclude(r => r.FieldValuesJson).Exclude(r => r.TableValuesJson)
        .Exclude(r => r.SummarySourceJson).Exclude(r => r.Data).Exclude(r => r.SpecJson);
    private readonly Dictionary<string, string> _captures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _listingCaptures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AggregateFormPinDto> _forms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _catalogFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeEnumOptionSet> _listOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DynamicFormTemplate> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AggregateSourceHeader Header, WorkAssignmentReport Report, DynamicFormTemplate Template, AggregateSchema Schema)> _payloadBatch = new(StringComparer.Ordinal);
    private string? _payloadBatchScope;
    private readonly Dictionary<string, string> _unitNames = new(StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<string, AggregateDataWindowDeclarationDto?>> Windows(string kind, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (dates is IAggregateBatchDataWindowReader batch) return await batch.ReadManyAsync(kind, ids, ct);
        var result = new Dictionary<string, AggregateDataWindowDeclarationDto?>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct(StringComparer.Ordinal)) result[id] = await dates.ReadAsync(kind, id, ct);
        return result;
    }

    private async Task<AggregateSchema> SchemaAsync(DynamicFormTemplate template, CancellationToken ct)
    {
        var schema = AggregateNativePayloadAdapter.Schema(template);
        var fields = System.Text.Json.JsonSerializer.Deserialize<List<tdtd_be.DTOs.DynamicForms.DynamicFormFieldDto>>(
            template.FieldsJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
        var members = schema.Members.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var field in fields.Where(f => f.ValueSource?.SourceType == "ENUM_CATALOG"))
        {
            var catalog = await db.LabelEnumCatalogs.Find(c => c.Id == field.ValueSource!.CatalogId && !c.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_ENUM_CATALOG_UNAVAILABLE");
            if (catalog.Options.Count > 5000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            var fingerprint = AggregateDigest.Of(catalog);
            if (_catalogFingerprints.TryGetValue(catalog.Id, out var old) && old != fingerprint) throw new AggregatePreviewException("AGG_INPUT_STALE");
            _catalogFingerprints[catalog.Id] = fingerprint;
            members[field.Id!] = members[field.Id!] with { AllowedChoiceCodes = catalog.Options.Where(o => o.IsActive).Select(o => o.Code).ToArray() };
        }
        foreach (var id in AggregateNativePayloadAdapter.ListCatalogIds(template))
        {
            var catalog = await db.LabelEnumCatalogs.Find(c => c.Id == id && !c.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_ENUM_CATALOG_UNAVAILABLE");
            if (catalog.Options.Count > 5000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            var fingerprint = AggregateDigest.Of(catalog);
            if (_catalogFingerprints.TryGetValue(id, out var old) && old != fingerprint) throw new AggregatePreviewException("AGG_INPUT_STALE");
            _catalogFingerprints[id] = fingerprint;
            _listOptions[id] = new(id, catalog.Options.Where(o => o.IsActive).Select(o => o.Code).ToHashSet(StringComparer.Ordinal));
        }
        foreach (var table in tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson) ?? [])
        {
            if (members[table.Id!].List is not { } list) continue;
            var resolve = tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.CompileCellTypes(table);
            members[table.Id!] = members[table.Id!] with { List = list with { Fields = list.Fields.Select(field => {
                var spec = resolve(field.Id, null);
                return spec.ValueSource?.SourceType == "ENUM_CATALOG"
                    ? field with { AllowedChoiceCodes = _listOptions[spec.ValueSource.CatalogId!].Codes.ToArray() } : field;
            }).ToArray() } };
        }
        return schema with { Members = members };
    }

    internal async Task<AggregateReadContext> BootstrapAsync(string reportId, string actor, CancellationToken ct)
    {
        Id(reportId); Id(actor);
        var report = await db.WorkAssignmentReports.Find(x => x.Id == reportId && !x.IsDeleted)
            .Project<WorkAssignmentReport>(HeaderOnly).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        if (report.AssigneeUserId != actor) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        string bindingId;
        if (!string.IsNullOrEmpty(report.WorkReportPeriodId))
        {
            var period = await db.WorkReportPeriods.Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            bindingId = period.WorkTemplateAssigneeId;
        }
        else
        {
            var bindings = await db.WorkTemplateAssignees.Find(x => x.WorkId == report.WorkId
                && x.WorkAssignmentId == report.WorkAssignmentId && x.AssigneeUserId == actor
                && x.DynamicFormTemplateId == report.DynamicFormTemplateId && !x.IsDeleted).Limit(2).ToListAsync(ct);
            if (bindings.Count != 1) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
            bindingId = bindings[0].Id;
        }
        return await ReadContextAsync(new("ONCE", report.WorkId, report.WorkAssignmentId, bindingId,
            report.Id, report.WorkReportPeriodId, null, null, null, null, null, ""), actor, ct);
    }
    internal async Task<object> ListOptionsAsync(AggregateReadContext context, AggregateListOptionsQueryDto request, string actor, CancellationToken ct)
    {
        if (!AggregateMappingPolicy.Decide(AggregateAction.EditMapping, context.Authority).Allowed) throw new AggregatePreviewException("AGG_PREVIEW_FORBIDDEN");
        if (request.PageSize is < 1 or > 50 || request.Query?.Length > 200) throw new AggregatePreviewException("AGG_PAGE_SIZE_INVALID");
        _ = request.Form == context.TargetSchema.Pin ? context.TargetSchema : await ReadSchemaAsync(request.Form, context, actor, ct);
        var template = await Template(request.Form, ct);
        var table = tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson)?
            .SingleOrDefault(t => t.Id == request.ListId && t.Presentation?.Kind == "LIST" && t.Fields!.Any(f => f.Id == request.FieldId))
            ?? throw new AggregatePreviewException("AGG_LIST_FIELD_REF");
        var spec = tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.CompileCellTypes(table)(request.FieldId, null);
        if (spec.Type is not ("singleSelect" or "multiSelect")) throw new AggregatePreviewException("AGG_LIST_PREDICATE_TYPE");
        IReadOnlyList<AggregateEditorOptionDto> options;
        string? catalogId = null, catalogStamp = null;
        if (spec.ValueSource?.SourceType == "ENUM_CATALOG")
        {
            catalogId = spec.ValueSource.CatalogId;
            // As in L2, binding grants only options of this field. Deactivation blocks new
            // authoring but does not revoke an existing report binding; deletion does.
            var catalog = await db.LabelEnumCatalogs.Find(c => c.Id == catalogId && !c.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_ENUM_CATALOG_UNAVAILABLE");
            catalogStamp = AggregateDigest.Of(catalog);
            options = catalog.Options.Where(o => o.IsActive).OrderBy(o => o.Order).ThenBy(o => o.Code, StringComparer.Ordinal)
                .Select(o => new AggregateEditorOptionDto(o.Code, o.Label)).ToArray();
        }
        else options = (spec.Options ?? []).Select(o => new AggregateEditorOptionDto(o.Code!, o.Label ?? o.Code!)).ToArray();
        var fingerprint = AggregateDigest.Of(new { actor, context.AuthorizationFingerprint, context.Context, request.Form, request.ListId, request.FieldId, request.Query, catalogStamp });
        var offset = 0;
        if (request.Cursor is { } cursor)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] != fingerprint || !int.TryParse(parts[1], out offset) || offset < 0) throw new AggregatePreviewException("AGG_CURSOR_STALE");
        }
        var selected = options.Where(o => string.IsNullOrWhiteSpace(request.Query) || o.Code.Contains(request.Query, StringComparison.OrdinalIgnoreCase)
            || o.Label.Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (offset > selected.Length) throw new AggregatePreviewException("AGG_CURSOR_STALE");
        var items = selected.Skip(offset).Take(request.PageSize).ToArray();
        return new { request.ListId, request.FieldId, CatalogId = catalogId, Required = spec.Required == true,
            SpecialOptions = AggregateListChoicePolicy.SpecialOptions(request.FieldId, spec.Type == "singleSelect" ? "CHOICE_ONE" : "CHOICE_MANY", spec.Required == true),
            Total = selected.Length, Items = items,
            NextCursor = offset + items.Length < selected.Length ? fingerprint + ":" + (offset + items.Length) : null };
    }

    internal async Task<AggregateEditorFormDto> EditorSchemaAsync(AggregateReadContext context,
        AggregateFormPinDto pin, string actor, CancellationToken ct, bool includeReportOptions = false)
    {
        var schema = pin == context.TargetSchema.Pin ? context.TargetSchema : await ReadSchemaAsync(pin, context, actor, ct);
        var template = await Template(pin, ct);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var fields = System.Text.Json.JsonSerializer.Deserialize<List<tdtd_be.DTOs.DynamicForms.DynamicFormFieldDto>>(template.FieldsJson, json) ?? [];
        var sections = System.Text.Json.JsonSerializer.Deserialize<List<tdtd_be.DTOs.DynamicForms.DynamicFormSectionDto>>(template.SectionsJson, json) ?? [];
        var members = fields.Where(f => f.Type != "evidence").Select(f => {
            var type = schema.Members[f.Id!].Type;
            var options = f.ValueSource?.Options ?? f.Options ?? [];
            return new AggregateEditorMemberDto(f.Id!, f.Name ?? f.Id!, f.SectionId, f.Type ?? "", type,
                type != "UNSUPPORTED", type == "UNSUPPORTED" ? "AGG_EDITOR_TYPE_UNSUPPORTED" : null,
                options.Select(o => new AggregateEditorOptionDto(o.Code ?? "", o.Label ?? o.Code ?? "")).ToArray());
        }).ToList();
        foreach (var field in fields.Where(f => f.ValueSource?.SourceType == "ENUM_CATALOG"))
        {
            var catalog = await db.LabelEnumCatalogs.Find(x => x.Id == field.ValueSource!.CatalogId && !x.IsDeleted).FirstOrDefaultAsync(ct);
            var index = members.FindIndex(m => m.Id == field.Id);
            if (catalog == null) members[index] = members[index] with { Supported = false, ReasonCode = "AGG_ENUM_CATALOG_UNAVAILABLE" };
            else if(catalog.Options.Count > 5000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            else members[index] = members[index] with { Options = catalog.Options.Where(o => o.IsActive)
                .OrderBy(o => o.Order).Select(o => new AggregateEditorOptionDto(o.Code, o.Label)).ToArray() };
        }
        foreach (var table in tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson) ?? [])
        {
            var member = schema.Members[table.Id!];
            var resolve = tdtd_be.Services.DynamicForms.DynamicFormNativeTableDefinition.CompileCellTypes(table);
            if (member.List is { } list)
            {
                members.Add(new(member.Id, table.Name ?? member.Id, table.SectionId, "LIST", "LIST", true, null, [],
                    List: new(table.Presentation!.ItemLabel ?? "Phần tử", list.Fields.Select(f => {
                        var spec = resolve(f.Id, null);
                        return new AggregateEditorListFieldDto(f.Id, table.Fields!.Single(c => c.Id == f.Id).Name ?? f.Id,
                            f.Type, spec.ValueSource?.SourceType == "ENUM_CATALOG" ? spec.ValueSource.CatalogId : null,
                            (spec.Options ?? []).Select(o => new AggregateEditorOptionDto(o.Code!, o.Label ?? o.Code!)).ToArray(), spec.Required == true,
                            AggregateListChoicePolicy.SpecialOptions(f.Id, f.Type, spec.Required == true));
                    }).ToArray())));
                continue;
            }
            var options = (table.Layout == "matrix" ? table.Rows!.Select(r => r.Id) : new string?[] { null })
                .Select(row => (IReadOnlyList<IReadOnlyList<AggregateEditorOptionDto>>)table.Fields!.Select(column =>
                    (IReadOnlyList<AggregateEditorOptionDto>)(resolve(column.Id!, row).Options ?? []).Select(o => new AggregateEditorOptionDto(o.Code!, o.Label ?? o.Code!)).ToArray()).ToArray()).ToArray();
            members.Add(new(member.Id, table.Name ?? member.Id, table.SectionId, "TABLE", "TABLE", true, null, [],
                new(table.Layout!, table.Fields!.Select(c => new AggregateEditorTableAxisDto(c.Id!, c.Name ?? c.Id!)).ToArray(),
                    table.Rows!.Select(r => new AggregateEditorTableAxisDto(r.Id!, r.Name ?? r.Id!)).ToArray(), member.Table!.CellTypes, options)));
        }
        AggregateEditorReportOptionsDto? reportOptions = null;
        if (includeReportOptions)
        {
            var listing = await ListPickerSourcesAsync(context, pin, actor, ct);
            var unitIds = listing.Headers.Select(h => h.UnitId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
            var units = await db.Units.Find(u => unitIds.Contains(u.Id) && !u.IsDeleted).ToListAsync(ct);
            reportOptions = new(listing.Headers.Select(h => h.PeriodKey).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => new AggregateEditorOptionDto(p!, p!)).ToArray(),
                unitIds.Select(id => new AggregateEditorOptionDto(id, units.SingleOrDefault(u => u.Id == id)?.FullName ?? id)).ToArray());
        }
        return new(pin, template.Name, sections.Select(s => new AggregateEditorSectionDto(s.Id!, s.Title ?? s.Id!)).ToArray(), members, reportOptions);
    }

    public async Task<AggregateReadContext> ReadContextAsync(AggregatePeriodContextDto selector, string actor, CancellationToken ct)
    {
        if (selector.View != null) return await ReadViewContextAsync(selector, actor, ct);
        Id(selector.WorkId); Id(selector.AssignmentId); Id(selector.BindingId); Id(selector.ReportId); Id(actor);
        var user = await db.Users.Find(x => x.Id == actor && !x.IsDeleted).FirstOrDefaultAsync(ct);
        var assignment = await db.WorkAssignments.Find(x => x.Id == selector.AssignmentId && x.WorkId == selector.WorkId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        var binding = await db.WorkTemplateAssignees.Find(x => x.Id == selector.BindingId && x.WorkId == selector.WorkId
            && x.WorkAssignmentId == selector.AssignmentId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        var report = await db.WorkAssignmentReports.Find(x => x.Id == selector.ReportId && x.WorkId == selector.WorkId
            && x.WorkAssignmentId == selector.AssignmentId && !x.IsDeleted).Project<WorkAssignmentReport>(HeaderOnly).FirstOrDefaultAsync(ct);
        if (user == null || assignment == null || binding == null || report == null || binding.AssigneeUserId != actor
            || !await CanRead(assignment, report, actor, ct) || DynamicFlowBranchVisibility.IsFlowAssignment(assignment))
            throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var period = string.IsNullOrEmpty(report.WorkReportPeriodId) ? null : await db.WorkReportPeriods.Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (!report.IsCurrent || report.AssigneeUserId != binding.AssigneeUserId
            || (!string.IsNullOrEmpty(report.WorkReportPeriodId) && period == null)
            || (period != null && (period.WorkTemplateAssigneeId != binding.Id || period.CurrentReportId != report.Id
            || period.WorkId != selector.WorkId || period.WorkAssignmentId != assignment.Id))) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        var pin = Pin(report);
        if (binding.DynamicFormTemplateId != pin.FormId) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        var template = await Template(pin, ct);
        var schema = await SchemaAsync(template, ct);
        var declaration = await dates.ReadAsync("REPORT", report.Id, ct);
        var schedule = AggregateDigest.Of(new { binding.Schedule, binding.AssignmentType, binding.StartDate, binding.DueDate, binding.IsActive, assignment.ParentAssignmentId });
        var context = new AggregatePeriodContextDto(binding.AssignmentType == "ONCE" ? "ONCE" : "PERIODIC", report.WorkId,
            report.WorkAssignmentId, binding.Id, report.Id, period?.Id, report.PeriodInstanceKey, report.PeriodKey,
            declaration?.StartDate, declaration?.EndDate, report.DueAtUtc?.ToUniversalTime().ToString("O"), schedule);
        var auth = AggregateDigest.Of(new { actor, user.UnitId, assignment.Assignees, binding.AssigneeUserId, assignment.CreatedByUserId, assignment.CurrentReviewerUserId, assignment.LeaderWatcherUserIds,
            choices = schema.Members.Values.Where(m => m.AllowedChoiceCodes != null).OrderBy(m => m.Id).Select(m => new { m.Id, m.AllowedChoiceCodes }) });
        var work = await db.Works.Find(w => w.Id == selector.WorkId && !w.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var mutationOpen = !work.CompletedAtUtc.HasValue && work.Status != WorkStatus.S3 && !assignment.CompletedAtUtc.HasValue;
        var ancestorId = assignment.ParentAssignmentId;
        var visitedAncestors = new HashSet<string>();
        while (!string.IsNullOrEmpty(ancestorId))
        {
            if (!visitedAncestors.Add(ancestorId)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var ancestor = await db.WorkAssignments.Find(a => a.Id == ancestorId && a.WorkId == work.Id && !a.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            mutationOpen &= !ancestor.CompletedAtUtc.HasValue && ancestor.IsActive;
            ancestorId = ancestor.ParentAssignmentId;
        }
        var facts = new AggregateAuthorityFacts(true, true, true, true, true, false,
            binding.AssigneeUserId == actor, binding.AssigneeUserId == actor, tdtd_be.Services.WorkAssignments.Internal.WorkAssignmentCurrentAuthority.IsReviewer(assignment, actor),
            true, mutationOpen && (v2Enabled || !StatConfigPhaseBarrier.IsBlocked(StatConfigPhaseBarrierEntries.P9Run)), assignment.IsActive && report.IsActive && binding.IsActive,
            report.Status.ToString(), false, true, true, true, true);
        if (!WorkReportPayloadConsistency.IsReadyForStatisticProjection(report))
            throw new AggregatePreviewException("AGG_TARGET_PAYLOAD_UNAVAILABLE");
        var payload = await payloadReader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        var previous = AggregateNativePayloadAdapter.Read(template, payload, schema, null, listOptions: _listOptions);
        return new(context, schema, facts, new(report.PayloadRevision, report.LifecycleRevision, 0, 0, pin.SchemaHash, ""), auth, declaration, previous);
    }
    public async Task<AggregateSchema> ReadSchemaAsync(AggregateFormPinDto pin, AggregateReadContext context, string actor, CancellationToken ct)
    {
        var children = await Children(context, actor, ct);
        var ids = children.Select(c => c.Id).ToArray();
        if (!await db.WorkTemplateAssignees.Find(x => ids.Contains(x.WorkAssignmentId) && x.WorkId == context.Context.WorkId
            && x.DynamicFormTemplateId == pin.FormId && x.DynamicFormFamilyId == pin.FamilyId
            && x.DynamicFormVersionNo == pin.VersionNo && x.DynamicFormSchemaHash == pin.SchemaHash && !x.IsDeleted).AnyAsync(ct)) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        return await SchemaAsync(await Template(pin, ct), ct);
    }
    public Task<AggregateSourceListing> ListSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct)
        => ListSourcesCoreAsync(context, form, actor, true, ct);
    internal Task<AggregateSourceListing> ListPickerSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct)
        => ListSourcesCoreAsync(context, form, actor, false, ct);
    internal async Task<IReadOnlyList<AggregateEditorSourceFormDto>> ListSourceFormsAsync(AggregateReadContext context, string actor, CancellationToken ct)
    {
        var ids = (await Children(context, actor, ct)).Select(c => c.Id).ToArray();
        var bindings = await db.WorkTemplateAssignees.Find(x => x.WorkId == context.Context.WorkId && ids.Contains(x.WorkAssignmentId) && !x.IsDeleted).Limit(10001).ToListAsync(ct);
        if (bindings.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var result = new List<AggregateFormPinDto>();
        foreach (var binding in bindings)
        {
            if (binding.DynamicFormTemplateId == null || binding.DynamicFormFamilyId == null || binding.DynamicFormVersionNo == null || binding.DynamicFormSchemaHash == null)
                throw new AggregatePreviewException("AGG_SCHEMA_PIN_UNAVAILABLE");
            result.Add(new(binding.DynamicFormTemplateId, binding.DynamicFormFamilyId, binding.DynamicFormVersionNo.Value, binding.DynamicFormSchemaHash));
        }
        var pins = result.Distinct().OrderBy(p => p.FormId, StringComparer.Ordinal).ToArray();
        var formIds = pins.Select(p => p.FormId).ToArray();
        var templates = await db.DynamicFormTemplates.Find(t => formIds.Contains(t.Id) && !t.IsDeleted)
            .Project(t => new { t.Id, t.Name, t.FamilyId, t.VersionNo, t.PublishedSchemaHash }).ToListAsync(ct);
        return pins.Select(p =>
        {
            var template = templates.SingleOrDefault(t => t.Id == p.FormId && t.FamilyId == p.FamilyId
                && t.VersionNo == p.VersionNo && t.PublishedSchemaHash == p.SchemaHash)
                ?? throw new AggregatePreviewException("AGG_SCHEMA_PIN_UNAVAILABLE");
            return new AggregateEditorSourceFormDto(p.FormId, p.FamilyId, p.VersionNo, p.SchemaHash, template.Name);
        }).ToArray();
    }
    private async Task<AggregateSourceListing> ListSourcesCoreAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, bool includeCoverage, CancellationToken ct)
    {
        var children = await Children(context, actor, ct);
        var ids = children.Select(c => c.Id).ToArray();
        var bindings = await db.WorkTemplateAssignees.Find(x => x.WorkId == context.Context.WorkId && ids.Contains(x.WorkAssignmentId)
            && x.DynamicFormTemplateId == form.FormId && !x.IsDeleted).Limit(10001).ToListAsync(ct);
        if (bindings.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var bindingIds = bindings.Select(b => b.Id).ToArray();
        var periods = await db.WorkReportPeriods.Find(x => x.WorkId == context.Context.WorkId && bindingIds.Contains(x.WorkTemplateAssigneeId) && !x.IsDeleted).Limit(20001).ToListAsync(ct);
        var reports = await db.WorkAssignmentReports.Find(x => x.WorkId == context.Context.WorkId && ids.Contains(x.WorkAssignmentId)
            && x.DynamicFormTemplateId == form.FormId && x.IsCurrent && !x.IsDeleted).Project<WorkAssignmentReport>(HeaderOnly).Limit(10001).ToListAsync(ct);
        if (periods.Count > 20000 || reports.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var childrenById = children.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var periodsById = periods.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var bindingsById = bindings.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var bindingsByAssignee = bindings.ToLookup(b => (b.WorkAssignmentId, b.AssigneeUserId));
        var periodsByBinding = periods.ToLookup(p => p.WorkTemplateAssigneeId);
        var reportWindows = await Windows("REPORT", reports.Select(r => r.Id).ToArray(), ct);
        foreach (var group in bindings.Select(b => b.AssigneeUnitId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Chunk(256))
        {
            var names = await db.Units.Find(u => group.Contains(u.Id) && !u.IsDeleted).ToListAsync(ct);
            foreach (var id in group) _unitNames[id!] = names.FirstOrDefault(u => u.Id == id)?.FullName ?? id!;
        }
        var headers = new List<AggregateSourceHeader>(); var slots = new List<AggregateSlot>();
        foreach (var report in reports)
        {
            var child = childrenById[report.WorkAssignmentId];
            if (!await CanRead(child, report, actor, ct)) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            var period = report.WorkReportPeriodId == null ? null : periodsById.GetValueOrDefault(report.WorkReportPeriodId);
            var candidates = (period != null
                ? bindingsById.TryGetValue(period.WorkTemplateAssigneeId, out var found) ? new[] { found } : []
                : bindingsByAssignee[(child.Id, report.AssigneeUserId)].ToArray())
                .Where(b => b.WorkAssignmentId == child.Id && b.DynamicFormTemplateId == report.DynamicFormTemplateId).ToArray();
            if (candidates.Length != 1) throw new AggregatePreviewException("AGG_SOURCE_BINDING_AMBIGUOUS");
            if (Pin(report) != form) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
            var binding = candidates[0];
            var header = await Header(report, child, binding, actor, ct, period, reportWindows[report.Id], true);
            headers.Add(header); _captures[report.Id] = AggregateDigest.Of(header);
        }
        foreach (var binding in includeCoverage ? bindings : [])
        {
            var child = childrenById[binding.WorkAssignmentId];
            var bindingPeriods = periodsByBinding[binding.Id].ToArray();
            var effectiveStart = binding.StartDate ?? child.StartDate;
            var effectiveEnd = binding.DueDate ?? child.DueDate;
            if (effectiveStart == null) throw new AggregatePreviewException("AGG_SCHEDULE_EFFECTIVE_RANGE_UNRESOLVED");
            var scheduleRevision = AggregateDigest.Of(new { binding.Schedule, effectiveStart, effectiveEnd, binding.AssigneeUserId, binding.IsActive });
            if (binding.AssignmentType == "ONCE" && bindingPeriods.Length > 1) throw new AggregatePreviewException("AGG_CURRENT_REPORT_AMBIGUOUS");
            var occurrences = new HashSet<string>(binding.AssignmentType == "ONCE" ? ["ONCE"] : bindingPeriods.Select(p => p.PeriodKey), StringComparer.Ordinal);
            // These are occurrence identities ONLY. Their data windows come from declarations.
            if (binding.AssignmentType != "ONCE")
            {
                // Never claim complete coverage after an arbitrary rolling horizon.
                var through = effectiveEnd ?? throw new AggregatePreviewException("AGG_SCHEDULE_HORIZON_UNRESOLVED");
                if ((through - effectiveStart.Value).TotalDays > 3660) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
                if (binding.Schedule == null || !ScheduleValidator.IsValid(binding.Schedule)) throw new AggregatePreviewException("AGG_SCHEDULE_INVALID");
                foreach (var due in AssignmentScheduleTimeHelper.GetDueDatesInRange(binding.Schedule, effectiveStart.Value, through))
                    occurrences.Add(AssignmentScheduleTimeHelper.GetPeriodKey(binding.Schedule, due));
            }
            else if (occurrences.Count == 0) occurrences.Add("ONCE");
            var periodsByOccurrence = bindingPeriods.ToLookup(p => p.PeriodKey);
            foreach (var occurrence in occurrences)
            {
                ct.ThrowIfCancellationRequested();
                if (slots.Count >= 20000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
                var period = binding.AssignmentType == "ONCE" ? bindingPeriods.SingleOrDefault() : periodsByOccurrence[occurrence].SingleOrDefault();
                var day = DateOnly.FromDateTime(effectiveStart.Value);
                if (occurrence != "ONCE" && !DateOnly.TryParseExact(occurrence, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day))
                    throw new AggregatePreviewException("AGG_OCCURRENCE_UNRESOLVED");
                var key = binding.Id + ":" + occurrence;
                slots.Add(new(key, binding.Id, scheduleRevision, occurrence, period?.Id, period?.CurrentReportId, form, true,
                    DateOnly.FromDateTime(effectiveStart.Value), effectiveEnd.HasValue ? DateOnly.FromDateTime(effectiveEnd.Value) : null, day, null));
            }
        }
        // Batch declarations across bindings, not one query per unit on every job checkpoint/read.
        // Keep missing declarations null and include all of them in the unchanged membership digest.
        var slotWindows = await Windows("SLOT", slots.Select(s => s.Key).ToArray(), ct);
        slots = slots.Select(s => s with { DataWindow = slotWindows[s.Key] }).ToList();
        var membership = AggregateDigest.Of(new { children = children.OrderBy(c => c.Id).Select(c => new { c.Id, c.ParentAssignmentId, c.Assignees, c.IsActive, c.UpdatedAtUtc }),
            bindings = bindings.OrderBy(b => b.Id).Select(b => new { b.Id, b.Schedule, b.StartDate, b.DueDate, b.AssigneeUserId, b.UpdatedAtUtc }),
            periods = periods.OrderBy(p => p.Id).Select(p => new { p.Id, p.CurrentReportId, p.PeriodInstanceKey, p.PeriodKey, p.UpdatedAtUtc }),
            headers = headers.OrderBy(h => h.Pin.ReportId), slots = slots.OrderBy(s => s.Key) });
        if (_listingCaptures.TryGetValue(form.FormId, out var prior) && prior != membership) throw new AggregatePreviewException("AGG_INPUT_STALE");
        _forms[form.FormId] = form; _listingCaptures[form.FormId] = membership;
        return new(headers, slots, true, membership, children.Where(c => c.IsActive).SelectMany(c => c.Assignees).Select(a => a.UnitId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }
    public async Task PreparePayloadBatchAsync(AggregateReadContext context, IReadOnlyList<AggregateSourceHeader> headers, string actor, CancellationToken ct)
    {
        if (headers.Count > 64) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        _payloadBatch.Clear(); _payloadBatchScope = null;
        var reportIds = headers.Select(h => h.Pin.ReportId).ToArray();
        var assignmentIds = headers.Select(h => h.Pin.AssignmentId).Distinct().ToArray();
        var bindingIds = headers.Select(h => h.Pin.BindingId).Distinct().ToArray();
        var periodIds = headers.Select(h => h.Pin.WorkReportPeriodId).Where(id => id != null).ToArray();
        var reports = (await db.WorkAssignmentReports.Find(r => reportIds.Contains(r.Id) && r.WorkId == context.Context.WorkId && !r.IsDeleted && r.IsCurrent)
            .Project<WorkAssignmentReport>(HeaderOnly).ToListAsync(ct)).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var assignments = (await db.WorkAssignments.Find(a => assignmentIds.Contains(a.Id) && a.WorkId == context.Context.WorkId
            && a.ParentAssignmentId == AggregateTargetIdentity.Parent(context.Context) && !a.IsDeleted).ToListAsync(ct)).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var bindings = (await db.WorkTemplateAssignees.Find(b => bindingIds.Contains(b.Id) && b.WorkId == context.Context.WorkId && !b.IsDeleted)
            .ToListAsync(ct)).ToDictionary(b => b.Id, StringComparer.Ordinal);
        var periods = (await db.WorkReportPeriods.Find(p => periodIds.Contains(p.Id) && p.WorkId == context.Context.WorkId && !p.IsDeleted)
            .ToListAsync(ct)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var declarations = await Windows("REPORT", reportIds, ct);
        var schemas = new Dictionary<string, AggregateSchema>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            ct.ThrowIfCancellationRequested();
            if (!reports.TryGetValue(header.Pin.ReportId, out var report) || !assignments.TryGetValue(header.Pin.AssignmentId, out var child)
                || !bindings.TryGetValue(header.Pin.BindingId, out var binding) || !await CanRead(child, report, actor, ct))
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            var period = report.WorkReportPeriodId == null ? null : periods.GetValueOrDefault(report.WorkReportPeriodId);
            if (report.WorkReportPeriodId != null && period == null) throw new AggregatePreviewException("AGG_INPUT_STALE");
            var fresh = await Header(report, child, binding, actor, ct, period, declarations[report.Id], true);
            if (fresh != header) throw new AggregatePreviewException("AGG_INPUT_STALE");
            var template = await Template(header.Form, ct);
            if (!schemas.TryGetValue(template.Id, out var schema)) schemas[template.Id] = schema = AggregateNativePayloadAdapter.Schema(template);
            _payloadBatch.Add(report.Id, (fresh, report, template, schema));
        }
        _payloadBatchScope = AggregateDigest.Of(new { context.Context, actor });
    }
    public async Task<AggregatePayload> ReadPayloadAsync(AggregateReadContext context, AggregateSourceHeader header, AggregateSchema schema, string actor, CancellationToken ct)
    {
        if (_payloadBatchScope == AggregateDigest.Of(new { context.Context, actor })
            && _payloadBatch.TryGetValue(header.Pin.ReportId, out var captured))
        {
            if (captured.Header != header) throw new AggregatePreviewException("AGG_INPUT_STALE");
            var batchSchema = captured.Schema;
            if (schema.Members.Any(member => !batchSchema.Members.TryGetValue(member.Key, out var current)
                || AggregateDigest.Of(current) != AggregateDigest.Of(member.Value))) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
            var snapshot = await payloadReader.LoadReportPayloadAsync(captured.Report, ct);
            WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(captured.Report, snapshot);
            return new(captured.Header.Pin, AggregateNativePayloadAdapter.Read(captured.Template, snapshot, schema, header,
                await ReadExternalTables(snapshot, schema, header.Pin.ReportId, ct), _listOptions));
        }
        var report = await db.WorkAssignmentReports.Find(x => x.Id == header.Pin.ReportId && !x.IsDeleted && x.IsCurrent).Project<WorkAssignmentReport>(HeaderOnly).FirstOrDefaultAsync(ct);
        var child = await db.WorkAssignments.Find(x => x.Id == header.Pin.AssignmentId && x.ParentAssignmentId == AggregateTargetIdentity.Parent(context.Context)
            && x.WorkId == context.Context.WorkId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        var binding = await db.WorkTemplateAssignees.Find(x => x.Id == header.Pin.BindingId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (report == null || child == null || binding == null || !await CanRead(child, report, actor, ct)) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        var fresh = await Header(report, child, binding, actor, ct);
        if (fresh != header) throw new AggregatePreviewException("AGG_INPUT_STALE");
        var template = await Template(schema.Pin, ct);
        var currentSchema = AggregateNativePayloadAdapter.Schema(template);
        if (schema.Members.Any(member => !currentSchema.Members.TryGetValue(member.Key, out var current)
            || AggregateDigest.Of(current) != AggregateDigest.Of(member.Value))) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var payload = await payloadReader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        return new(fresh.Pin, AggregateNativePayloadAdapter.Read(template, payload, schema, header,
            await ReadExternalTables(payload, schema, header.Pin.ReportId, ct), _listOptions));
    }
    private async Task<IReadOnlyDictionary<string, AggregateTable>> ReadExternalTables(WorkReportPayloadSnapshot payload,
        AggregateSchema schema, string reportId, CancellationToken ct)
    {
        var result = new Dictionary<string, AggregateTable>();
        if (string.IsNullOrWhiteSpace(payload.TableValuesJson)) return result;
        using var doc = JsonDocument.Parse(payload.TableValuesJson);
        if (!doc.RootElement.TryGetProperty("nativeTables", out var native)) return result;
        foreach (var table in native.GetProperty("tables").EnumerateArray())
        {
            var id = table.GetProperty("tableId").GetString()!;
            if (!schema.Members.TryGetValue(id, out var member) || member.Table == null
                || !table.TryGetProperty("contentRef", out var external)) continue;
            var reference = external.Deserialize<AggregateContentReference>(AggregateCanonical.Json)
                ?? throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
            result.Add(id, await new AggregateContentTableStore(db).ReadTable(reportId, reference, member.Table, ct));
        }
        return result;
    }
    public async Task<bool> IsCurrentAsync(AggregateReadContext context, IReadOnlyList<AggregateSourcePinDto> pins, string membershipRevision, string actor, CancellationToken ct)
    {
        _payloadBatch.Clear(); _payloadBatchScope = null;
        // A cached Form is immutable only while its full structure still agrees;
        // check fresh documents, including same-hash corruption, before exposing results.
        foreach (var captured in _templates.Values.ToArray())
        {
            var freshTemplate = await db.DynamicFormTemplates.Find(t => t.Id == captured.Id && !t.IsDeleted).FirstOrDefaultAsync(ct);
            if (freshTemplate == null || AggregateDigest.Of(freshTemplate) != AggregateDigest.Of(captured)) return false;
        }
        foreach (var item in _catalogFingerprints.ToArray())
        {
            var catalog = await db.LabelEnumCatalogs.Find(c => c.Id == item.Key && !c.IsDeleted).FirstOrDefaultAsync(ct);
            if (catalog == null || AggregateDigest.Of(catalog) != item.Value) return false;
        }
        var fresh = await ReadContextAsync(context.Context, actor, ct);
        if (fresh.Context != context.Context || fresh.Revisions != context.Revisions || fresh.DataWindow != context.DataWindow
            || fresh.AuthorizationFingerprint != context.AuthorizationFingerprint) return false;
        var before = _listingCaptures.ToDictionary(p => p.Key, p => p.Value);
        var captures = _captures.ToDictionary(p => p.Key, p => p.Value);
        foreach (var form in _forms.Values.ToArray())
        {
            var listing = await ListSourcesAsync(context, form, actor, ct);
            if (listing.MembershipRevision != before[form.FormId]) return false;
        }
        return pins.All(pin => captures.TryGetValue(pin.ReportId, out var value) && _captures.TryGetValue(pin.ReportId, out var current) && value == current);
    }
    private async Task<AggregateSourceHeader> Header(WorkAssignmentReport report, WorkAssignment child, WorkTemplateAssignee binding, string actor, CancellationToken ct, WorkReportPeriod? period = null,
        AggregateDataWindowDeclarationDto? capturedWindow = null, bool windowCaptured = false)
    {
        if (report.WorkId != child.WorkId || report.WorkAssignmentId != child.Id || binding.WorkId != child.WorkId
            || binding.WorkAssignmentId != child.Id || binding.AssigneeUserId != report.AssigneeUserId
            || binding.DynamicFormTemplateId != report.DynamicFormTemplateId)
            throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        var auth = AggregateDigest.Of(new { actor, child.CreatedByUserId, child.CurrentReviewerUserId, child.LeaderWatcherUserIds, child.Assignees, binding.AssigneeUserId });
        var relationship = AggregateDigest.Of(new { child.WorkId, child.ParentAssignmentId, child.Path, child.Assignees });
        if (!string.IsNullOrWhiteSpace(report.WorkReportPeriodId))
        {
            period ??= await db.WorkReportPeriods.Find(p => p.Id == report.WorkReportPeriodId && p.WorkId == child.WorkId
                && p.WorkAssignmentId == child.Id && p.WorkTemplateAssigneeId == binding.Id && !p.IsDeleted).FirstOrDefaultAsync(ct);
            if (period == null || period.Id != report.WorkReportPeriodId || period.WorkId != child.WorkId || period.WorkAssignmentId != child.Id
                || period.WorkTemplateAssigneeId != binding.Id || period.CurrentReportId != report.Id)
                throw new AggregatePreviewException("AGG_INPUT_STALE");
        }
        // Same classification policy as the report editor. These schedule bounds identify
        // backfill only; they are never substituted for a declared aggregation data window.
        var reportDate = (report.ReportDate ?? period?.ReportDate)?.Date;
        var start = (report.PeriodStart ?? period?.PeriodStart ?? reportDate)?.Date;
        var end = (report.PeriodEnd ?? period?.PeriodEnd ?? reportDate ?? start)?.Date;
        var anchor = (report.DueAtUtc ?? period?.DueAtUtc ?? reportDate)?.Date;
        var historical = report.IsHistoricalData || period?.IsHistoricalData == true ||
            tdtd_be.Services.WorkAssignments.Internal.WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(child, start, end, anchor, DateTime.UtcNow);
        var pin = new AggregateSourcePinDto(report.WorkId, report.WorkAssignmentId, binding.Id, report.WorkReportPeriodId, report.PeriodInstanceKey,
            report.Id, report.VersionNo, report.PayloadRevision, report.LifecycleRevision, report.PayloadHash ?? "", report.DynamicFormSchemaHash ?? "",
            report.Status.ToString(), report.IsCurrent, relationship, auth);
        return new(pin, child.ParentAssignmentId!, Pin(report), binding.AssigneeUnitId ?? "", binding.AssignmentType == "ONCE" ? "ONCE" : report.PeriodKey, true, report.IsActive, report.IsDeleted,
            windowCaptured ? capturedWindow : await dates.ReadAsync("REPORT", report.Id, ct)) { IsHistoricalData = historical,
                CompletedDate = report.CompletedDate ?? period?.CompletedDate, SubmittedAtUtc = report.SubmittedAtUtc,
                DueAtUtc = report.DueAtUtc ?? period?.DueAtUtc, PeriodKey = report.PeriodKey,
                UnitName = _unitNames.GetValueOrDefault(binding.AssigneeUnitId ?? "") ?? binding.AssigneeUnitId,
                ReportTitle = report.ReportTitle };
    }
    private async Task<List<WorkAssignment>> Children(AggregateReadContext context, string actor, CancellationToken ct)
    {
        var children = await db.WorkAssignments.Find(x => x.WorkId == context.Context.WorkId && x.ParentAssignmentId == AggregateTargetIdentity.Parent(context.Context) && !x.IsDeleted).Limit(10001).ToListAsync(ct);
        if (children.Count > 10000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        foreach (var child in children)
            // The shared helper already grants the assignment creator direct read access.
            // Use the freshly fetched assignment for that case; keep its full projected/ancestor policy otherwise.
            if (DynamicFlowBranchVisibility.IsFlowAssignment(child) || (!tdtd_be.Services.WorkAssignments.Internal.WorkAssignmentCurrentAuthority.IsReviewer(child, actor)
                && !await WorkAssignmentReadAccessHelper.CanReadAssignmentOrAncestorAsync(db, child, actor, ct)))
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        return children; // Do not drop historical approved sources because the assignment is inactive.
    }
    private async Task<bool> CanRead(WorkAssignment assignment, WorkAssignmentReport report, string actor, CancellationToken ct)
    {
        if (DynamicFlowBranchVisibility.IsFlowAssignment(assignment)) return false; // No whole-report Flow capability in this slice.
        return report.AssigneeUserId == actor || tdtd_be.Services.WorkAssignments.Internal.WorkAssignmentCurrentAuthority.IsReviewer(assignment, actor)
            || await WorkAssignmentReadAccessHelper.CanReadAssignmentOrAncestorAsync(db, assignment, actor, ct);
    }
    private async Task<DynamicFormTemplate> Template(AggregateFormPinDto pin, CancellationToken ct)
    {
        Id(pin.FormId);
        if (!_templates.TryGetValue(pin.FormId, out var template))
            template = await db.DynamicFormTemplates.Find(x => x.Id == pin.FormId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (template == null || template.FamilyId != pin.FamilyId || template.VersionNo != pin.VersionNo || template.PublishedSchemaHash != pin.SchemaHash)
            throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        _templates[pin.FormId] = template;
        return template;
    }
    private static AggregateFormPinDto Pin(WorkAssignmentReport report) => new(report.DynamicFormTemplateId ?? "", report.DynamicFormFamilyId ?? "", report.DynamicFormVersionNo ?? 0, report.DynamicFormSchemaHash ?? "");
    private static void Id(string? id) { if (id == null || !ObjectId.TryParse(id, out _)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE"); }
}
