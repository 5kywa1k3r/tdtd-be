using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    // Resolve only submitted unit IDs. The native validator compares every selected value
    // against this active set before a draft or submission can be written.
    private async Task<IReadOnlySet<string>> LoadNativeListUnitIdsAsync(
        DynamicFormTemplate form, string? tableValuesJson, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!DynamicFormNativeTableDefinition.IsNative(form) || string.IsNullOrWhiteSpace(tableValuesJson)
            || Encoding.UTF8.GetByteCount(tableValuesJson) > Runtime.DynamicFormNativeTableValues.MaximumBytes)
            return ids;
        DynamicFormNativeTableDefinition.ValidateStored(form);
        try
        {
            var definitions = DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson)!;
            var unitFields = definitions.Where(table => table.Presentation?.Kind == "LIST")
                .ToDictionary(table => table.Id!, table => {
                    var types = DynamicFormNativeTableDefinition.CompileCellTypes(table);
                    return table.Fields!.Where(field => types(field.Id!, null).ValueSource?.SourceType == "SYSTEM_UNIT")
                        .Select(field => field.Id!).ToHashSet(StringComparer.Ordinal);
                }, StringComparer.Ordinal);
            using var document = JsonDocument.Parse(tableValuesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("nativeTables", out var native)
                || native.ValueKind != JsonValueKind.Object || !native.TryGetProperty("tables", out var tables)
                || tables.ValueKind != JsonValueKind.Array)
                return ids;
            foreach (var table in tables.EnumerateArray())
            {
                if (table.ValueKind != JsonValueKind.Object || !table.TryGetProperty("tableId", out var tableId)
                    || tableId.ValueKind != JsonValueKind.String
                    || !unitFields.TryGetValue(tableId.GetString()!, out var fields) || fields.Count == 0
                    || !table.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var record in records.EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("cells", out var cells)
                        || cells.ValueKind != JsonValueKind.Object) continue;
                    foreach (var fieldId in fields)
                    {
                        if (!cells.TryGetProperty(fieldId, out var cell) || cell.ValueKind != JsonValueKind.Object
                            || !cell.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
                            || state.GetString() != "value"
                            || !cell.TryGetProperty("value", out var value)) continue;
                        if (value.ValueKind == JsonValueKind.String && ObjectId.TryParse(value.GetString(), out _))
                            ids.Add(value.GetString()!);
                        else if (value.ValueKind == JsonValueKind.Array)
                            foreach (var entry in value.EnumerateArray())
                                if (entry.ValueKind == JsonValueKind.String && ObjectId.TryParse(entry.GetString(), out _))
                                    ids.Add(entry.GetString()!);
                    }
                }
            }
        }
        catch (JsonException) { return ids; } // Native validation reports the malformed payload.
        if (ids.Count == 0) return ids;
        var active = await _ctx.Units.Find(x => ids.Contains(x.Id) && !x.IsDeleted && x.Code != "ROOT")
            .Project(x => x.Id).ToListAsync(ct);
        return active.ToHashSet(StringComparer.Ordinal);
    }
}
