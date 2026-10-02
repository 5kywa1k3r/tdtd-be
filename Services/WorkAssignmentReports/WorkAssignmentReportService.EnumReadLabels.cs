using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    // A report reader may see the labels of values in that report. This does not
    // grant the catalog search/edit capability used by the reporter's picker.
    public async Task<ReportSelectedEnumLabelsResponse> GetSelectedEnumLabelsAsync(
        string reportId, string actorUserId, CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var report = await _ctx.WorkAssignmentReports.Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw ReportNotFound(reportId);
        await EnsureReportAccessAsync(report, actorUserId, ct);
        await HydrateReportPayloadAsync(report, ct);

        var form = await _ctx.DynamicFormTemplates.Find(x => x.Id == report.DynamicFormTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw InvalidReportRuntimePayload(report, "dynamicFormTemplate", "DYNAMIC_FORM_RUNTIME_FORM_NOT_FOUND");
        EnsureRuntimeFormVersionBinding(report, form);

        var result = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        if (!DynamicFormNativeTableDefinition.IsNative(form) || string.IsNullOrWhiteSpace(report.TableValuesJson))
            return new(report.Id, report.PayloadRevision, report.DynamicFormSchemaHash, result);

        var tables = DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson)!;
        var bound = new Dictionary<string, Dictionary<string, (string SourceType, string CatalogId, HashSet<string> Codes)>>(StringComparer.Ordinal);
        foreach (var table in tables.Where(x => x.Presentation?.Kind == "LIST"))
        {
            var fields = new Dictionary<string, (string SourceType, string CatalogId, HashSet<string> Codes)>(StringComparer.Ordinal);
            var types = DynamicFormNativeTableDefinition.CompileCellTypes(table);
            foreach (var field in table.Fields!)
            {
                var spec = types(field.Id!, null);
                if (spec.Type is not ("singleSelect" or "multiSelect") || spec.ValueSource is null) continue;
                var source = spec.ValueSource.SourceType;
                if (source == "ENUM_CATALOG" && !string.IsNullOrWhiteSpace(spec.ValueSource.CatalogId)
                    || (source is "SYSTEM_UNIT" or "SYSTEM_LOCALITY") && table.Presentation?.Kind == "LIST")
                    fields[field.Id!] = (source!, spec.ValueSource.CatalogId ?? "", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            if (fields.Count > 0) bound[table.Id!] = fields;
        }
        if (bound.Count == 0) return new(report.Id, report.PayloadRevision, report.DynamicFormSchemaHash, result);

        using var document = JsonDocument.Parse(report.TableValuesJson);
        if (!document.RootElement.TryGetProperty("nativeTables", out var native)
            || !native.TryGetProperty("tables", out var valueTables) || valueTables.ValueKind != JsonValueKind.Array)
            return new(report.Id, report.PayloadRevision, report.DynamicFormSchemaHash, result);
        if (native.TryGetProperty("schemaHash", out var hash)
            && !string.Equals(hash.GetString(), report.DynamicFormSchemaHash, StringComparison.Ordinal))
            throw InvalidReportRuntimePayload(report, "tableValuesJson", "NATIVE_SCHEMA_HASH_MISMATCH");

        foreach (var valueTable in valueTables.EnumerateArray())
        {
            if (!valueTable.TryGetProperty("tableId", out var tableIdElement)
                || !bound.TryGetValue(tableIdElement.GetString() ?? "", out var fields)
                || !valueTable.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Object) continue;
                foreach (var (fieldId, binding) in fields)
                {
                    if (!cells.TryGetProperty(fieldId, out var cell) || cell.ValueKind != JsonValueKind.Object
                        || !cell.TryGetProperty("state", out var state) || state.GetString() != "value"
                        || !cell.TryGetProperty("value", out var value)) continue;
                    if (value.ValueKind == JsonValueKind.String)
                        binding.Codes.Add(value.GetString() ?? "");
                    else if (value.ValueKind == JsonValueKind.Array)
                        foreach (var code in value.EnumerateArray())
                            if (code.ValueKind == JsonValueKind.String) binding.Codes.Add(code.GetString() ?? "");
                }
            }
        }

        var catalogIds = bound.Values.SelectMany(fields => fields.Values)
            .Where(binding => binding.Codes.Count > 0 && binding.SourceType == "ENUM_CATALOG").Select(binding => binding.CatalogId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var catalogs = await _ctx.LabelEnumCatalogs.Find(x => catalogIds.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct);
        var byId = catalogs.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var unitIds = bound.Values.SelectMany(fields => fields.Values)
            .Where(binding => binding.SourceType == "SYSTEM_UNIT").SelectMany(binding => binding.Codes)
            .Where(code => ObjectId.TryParse(code, out _)).Distinct(StringComparer.Ordinal).ToArray();
        var units = await _ctx.Units.Find(x => unitIds.Contains(x.Id) && x.Code != "ROOT")
            .Project(x => new { x.Id, x.FullName, x.Code }).ToListAsync(ct);
        var unitLabels = units.ToDictionary(x => x.Id, x => $"{x.FullName} - {x.Code}", StringComparer.Ordinal);
        var localityLabels = SystemLocalityCatalog.Official.Rows.ToDictionary(x => x.Code,
            x => $"{x.Name} - {x.Code}", StringComparer.Ordinal);
        foreach (var (tableId, fields) in bound)
        {
            foreach (var (fieldId, binding) in fields)
            {
                if (binding.Codes.Count == 0) continue;
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var code in binding.Codes)
                {
                    if (binding.SourceType == "SYSTEM_UNIT" && unitLabels.TryGetValue(code, out var unitLabel)) labels[code] = unitLabel;
                    else if (binding.SourceType == "SYSTEM_LOCALITY" && localityLabels.TryGetValue(code, out var localityLabel)) labels[code] = localityLabel;
                    else if (binding.SourceType == "ENUM_CATALOG" && byId.TryGetValue(binding.CatalogId, out var catalog))
                    {
                        var option = catalog.Options.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
                        if (option is not null) labels[code] = option.Label;
                    }
                }
                if (labels.Count == 0) continue;
                if (!result.TryGetValue(tableId, out var tableLabels))
                    result[tableId] = tableLabels = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                tableLabels[fieldId] = labels;
            }
        }
        return new(report.Id, report.PayloadRevision, report.DynamicFormSchemaHash, result);
    }
}
