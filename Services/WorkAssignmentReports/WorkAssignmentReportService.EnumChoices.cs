using MongoDB.Driver;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    public async Task<PagedResult<LabelEnumOptionPickRow>> SearchFieldEnumOptionsAsync(
        string? reportId, string? assignmentId, string fieldId, string catalogId,
        string? q, int page, int pageSize, string actorUserId, CancellationToken ct)
    {
        EnsureActor(actorUserId);
        if (!ObjectId.TryParse(reportId ?? assignmentId, out _) || !ObjectId.TryParse(catalogId, out _)
            || string.IsNullOrWhiteSpace(fieldId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_REQUEST_REQUIRED);
        WorkAssignmentReport report;
        if (!string.IsNullOrWhiteSpace(reportId))
        {
            report = await _ctx.WorkAssignmentReports.Find(x => x.Id == reportId && !x.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw ReportNotFound(reportId);
            var access = await EnsureReportAccessAsync(report, actorUserId, ct);
            if (!access.isAssignee || report.AssigneeUserId != actorUserId)
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN);
        }
        else
        {
            // Same server-owned binding used by report creation. No client form/version is accepted.
            var access = await EnsureAssignmentReportAccessAsync(assignmentId ?? "", actorUserId, ct);
            var binding = await _ctx.WorkTemplateAssignees.Find(x => x.WorkAssignmentId == access.assignment.Id
                && x.AssigneeUserId == actorUserId && x.IsActive && !x.IsDeleted).FirstOrDefaultAsync(ct);
            if (!access.isAssignee || binding is null || binding.WorkId != access.assignment.WorkId
                || binding.DynamicFormTemplateId != access.assignment.DynamicFormTemplateId
                || binding.DynamicFormVersionNo != access.assignment.DynamicFormVersionNo
                || binding.DynamicFormFamilyId != access.assignment.DynamicFormFamilyId
                || binding.DynamicFormSchemaHash != access.assignment.DynamicFormSchemaHash)
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN);
            report = new WorkAssignmentReport {
                WorkAssignmentId = access.assignment.Id, AssigneeUserId = actorUserId,
                DynamicFormTemplateId = binding.DynamicFormTemplateId,
                DynamicFormTemplateCode = binding.DynamicFormTemplateCode,
                DynamicFormFamilyId = binding.DynamicFormFamilyId,
                DynamicFormVersionNo = binding.DynamicFormVersionNo,
                DynamicFormSchemaHash = binding.DynamicFormSchemaHash
            };
        }
        var form = await _ctx.DynamicFormTemplates.Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw InvalidReportRuntimePayload(report, "dynamicFormTemplate", "DYNAMIC_FORM_RUNTIME_FORM_NOT_FOUND");
        EnsureRuntimeFormVersionBinding(report, form);
        var field = ReadRuntimeFields(form.FieldsJson).SingleOrDefault(x => x.Id == fieldId);
        var topLevelMatch = field is not null && IsRuntimeChoiceFieldType(field.FieldType)
            && string.Equals(field.EnumCatalogId, catalogId, StringComparison.Ordinal);
        var listMatch = false;
        if (!topLevelMatch && DynamicFormNativeTableDefinition.IsNative(form))
        {
            var tables = DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson)!;
            var owner = tables.SingleOrDefault(table => table.Presentation?.Kind == "LIST"
                && table.Fields!.Any(candidate => candidate.Id == fieldId));
            if (owner is not null)
            {
                var spec = DynamicFormNativeTableDefinition.CompileCellTypes(owner)(fieldId, null);
                listMatch = spec.Type is "singleSelect" or "multiSelect"
                    && spec.ValueSource?.SourceType == "ENUM_CATALOG"
                    && string.Equals(spec.ValueSource.CatalogId, catalogId, StringComparison.Ordinal);
            }
        }
        if (!topLevelMatch && !listMatch)
            throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_REPORT_ACCESS_FORBIDDEN);
        var catalog = await LoadBoundEnumCatalogAsync(catalogId, ct);
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var options = catalog.Options.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
            options = options.Where(x => x.Code.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
                || x.Label.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        // An exact selected code must not disappear behind the first search page.
        var rows = options.OrderBy(x => string.Equals(x.Code, q?.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Order).ThenBy(x => x.Code).ToList();
        return new PagedResult<LabelEnumOptionPickRow>(rows.Skip(page * pageSize).Take(pageSize)
            .Select(x => new LabelEnumOptionPickRow($"{catalog.Id}:{x.Code}", catalog.Id, catalog.Code, x.Code, x.Label, x.Order)).ToList(),
            rows.Count, page, pageSize);
    }

    // Call only with references extracted from a validated, server-bound published form.
    // Deactivation blocks new authoring, not use by existing assignments/reports.
    private async Task<LabelEnumCatalog> LoadBoundEnumCatalogAsync(string id, CancellationToken ct)
        => await _ctx.LabelEnumCatalogs.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.LABEL_ENUM_CATALOG_NOT_FOUND);

    private async Task<IReadOnlyDictionary<string, RuntimeEnumOptionSet>> LoadBoundEnumOptionSetsAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, RuntimeEnumOptionSet>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var catalog = await LoadBoundEnumCatalogAsync(id, ct);
            result[id] = new RuntimeEnumOptionSet(id, catalog.Options.Where(x => x.IsActive)
                .Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        return result;
    }
}
