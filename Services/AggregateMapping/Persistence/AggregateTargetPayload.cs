using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateTargetPayload
{
    internal static async Task<(string Fields, string Tables)> ValidateAsync(MongoDbContext db, IClientSessionHandle session,
        DynamicFormTemplate form, string targetId, AggregateTargetWrite write, string? fieldValuesJson, string? tableValuesJson,
        Func<string, AggregateContentReference, Task>? bindContent, CancellationToken ct)
    {
        var fields = JsonNode.Parse(string.IsNullOrWhiteSpace(fieldValuesJson) ? "{}" : fieldValuesJson)!.AsObject();
        var values = fields["values"]?.AsObject() ?? fields;
        var tables = JsonNode.Parse(string.IsNullOrWhiteSpace(tableValuesJson) ? "{}" : tableValuesJson)!.AsObject();
        var definitions = JsonSerializer.Deserialize<List<DynamicFormFieldDto>>(form.FieldsJson, AggregateCanonical.Json) ?? [];
        var tableDefinitions = DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson) ?? [];
        var target = write.Recipe.Nodes.Single(n => n.Kind == "TARGET");
        foreach (var member in write.Members)
        {
            var port = target.Inputs.Single(p => p.MemberId == member);
            var result = write.Preview.Preview.Results.Single(r => r.NodeId == target.Id && r.PortId == port.Id);
            if (result.State is not ("RESULT" or "NO_RESULT")) throw new AggregatePreviewException("AGG_PREVIEW_UNAVAILABLE");
            if (port.ValueType == "LIST")
            {
                var definition = tableDefinitions.Single(t => t.Id == member);
                if (definition.Presentation?.Kind != "LIST" || definition.Layout == "matrix") throw new AggregatePreviewException("AGG_LIST_TARGET_SCHEMA");
                AggregateListValue? list = null;
                if (result.Value is { } stored)
                {
                    list = stored.TryGetProperty("id", out _)
                        ? await new AggregateListStore(db).Load(targetId, stored.Deserialize<AggregateListReference>(AggregateCanonical.Json)!, ct)
                        : AggregateListWire.Decode(stored);
                }
                var records = AggregateNativePayloadAdapter.ListRecords(definition, list, row => AggregateListWire.OutputId(write.Instance.Id, member, row.Origin));
                var targetTables = tables["nativeTables"]?["tables"]?.AsArray() ?? throw new AggregatePreviewException("AGG_NATIVE_TABLE_ENVELOPE_REQUIRED");
                var targetIndex = targetTables.ToList().FindIndex(t => t!["tableId"]!.GetValue<string>() == member);
                if (targetIndex < 0) throw new AggregatePreviewException("AGG_NATIVE_TABLE_ENVELOPE_REQUIRED");
                targetTables[targetIndex] = new JsonObject { ["tableId"] = member, ["records"] = records };
            }
            else if (port.ValueType != "TABLE")
            {
                var definition = definitions.Single(f => f.Id == member);
                var proposed = new JsonObject { [member] = result.State == "NO_RESULT" ? null : Scalar(port.ValueType, result.Value) };
                IReadOnlyList<string>? choiceCodes = null;
                if (port.ValueType is "CHOICE_ONE" or "CHOICE_MANY")
                {
                    if (definition.ValueSource?.SourceType == "ENUM_CATALOG")
                    {
                        var catalog = await db.LabelEnumCatalogs.Find(session, c => c.Id == definition.ValueSource.CatalogId && !c.IsDeleted).FirstOrDefaultAsync(ct)
                            ?? throw new AggregatePreviewException("AGG_ENUM_CATALOG_UNAVAILABLE");
                        choiceCodes = catalog.Options.Where(o => o.IsActive).Select(o => o.Code).ToArray();
                    }
                    else choiceCodes = (definition.ValueSource?.Options ?? definition.Options ?? []).Select(o => o.Code!).ToArray();
                }
                var canonical = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(proposed.ToJsonString(),
                    [new(member, definition.Type!, Required: false, AllowedChoiceCodes: choiceCodes)], validateRequiredFields: false);
                var canonicalValues = JsonNode.Parse(canonical.CanonicalValuesJson)!.AsObject();
                if (!Equivalent(proposed[member], canonicalValues[member], port.ValueType)) throw new AggregatePreviewException("AGG_TARGET_VALUE_NOT_CANONICAL");
                if (definition.Key != member && definition.Key != null) values.Remove(definition.Key);
                values[member] = canonicalValues[member]?.DeepClone();
            }
            else
            {
                // NO_RESULT is explicitly empty; preserve the native matrix row schema.
                var definition = tableDefinitions.Single(t => t.Id == member);
                var matrix = definition.Layout == "matrix";
                if (result.Value is { } external && external.ValueKind == JsonValueKind.Object
                    && external.TryGetProperty("kind", out var kind) && kind.GetString() == AggregateContentTableStore.Kind)
                {
                    var reference = external.Deserialize<AggregateContentReference>(AggregateCanonical.Json)
                        ?? throw new AggregatePreviewException("AGG_CONTENT_TABLE_INVALID");
                    _ = await new AggregateContentTableStore(db).Manifest(targetId, reference, ct);
                    if (bindContent != null) await bindContent(member, reference);
                    var nativeList = tables["nativeTables"]?["tables"]?.AsArray() ?? throw new AggregatePreviewException("AGG_NATIVE_TABLE_ENVELOPE_REQUIRED");
                    var nativeIndex = nativeList.ToList().FindIndex(t => t!["tableId"]!.GetValue<string>() == member);
                    if (nativeIndex < 0) throw new AggregatePreviewException("AGG_NATIVE_TABLE_ENVELOPE_REQUIRED");
                    nativeList[nativeIndex] = new JsonObject { ["tableId"] = member, ["records"] = new JsonArray(), ["contentRef"] = JsonNode.Parse(external.GetRawText()) };
                    continue;
                }
                var rows = result.Value?.GetProperty("rows").EnumerateArray().ToArray() ?? [];
                var rowCount = matrix ? definition.Rows!.Count : rows.Length;
                if (result.State == "RESULT" && matrix && rows.Length != rowCount) throw new AggregatePreviewException("AGG_TABLE_SHAPE");
                var compiled = DynamicFormNativeTableDefinition.CompileCellTypes(definition);
                var records = new JsonArray();
                for (var r = 0; r < rowCount; r++)
                {
                    var rowId = matrix ? definition.Rows![r].Id! : "agg_" + AggregateCanonical.Key(write.Instance.Id, member, r.ToString(CultureInfo.InvariantCulture))[..24];
                    var cells = new JsonObject();
                    if (r < rows.Length)
                    {
                        var sourceCells = rows[r].GetProperty("cells").EnumerateArray().ToArray();
                        if (sourceCells.Length != definition.Fields!.Count) throw new AggregatePreviewException("AGG_TABLE_SHAPE");
                        for (var c = 0; c < sourceCells.Length; c++)
                        {
                            var cell = sourceCells[c]; if (cell.GetProperty("state").GetString() != "VALUE") continue;
                            var column = definition.Fields[c].Id!;
                            var spec = compiled(column, matrix ? rowId : null);
                            cells[column] = new JsonObject { ["type"] = spec.Type, ["state"] = "value",
                                ["value"] = Scalar(cell.GetProperty("type").GetString()!, cell.GetProperty("value")) };
                        }
                    }
                    records.Add(new JsonObject { [matrix ? "rowId" : "recordId"] = rowId, ["cells"] = cells });
                }
                var native = tables["nativeTables"]?.AsObject() ?? throw new AggregatePreviewException("AGG_NATIVE_TABLE_ENVELOPE_REQUIRED");
                var list = native["tables"]!.AsArray();
                var index = list.ToList().FindIndex(t => t!["tableId"]!.GetValue<string>() == member);
                if (index < 0) throw new AggregatePreviewException("AGG_NATIVE_TABLE_ENVELOPE_REQUIRED");
                list[index] = new JsonObject { ["tableId"] = member, [matrix ? "rows" : "records"] = records };
            }
        }
        var fieldJson = fields.ToJsonString(); var tableJson = tables.ToJsonString();
        var optionSets = new Dictionary<string, RuntimeEnumOptionSet>(StringComparer.Ordinal);
        foreach (var id in AggregateNativePayloadAdapter.ListCatalogIds(form))
        {
            var catalog = await db.LabelEnumCatalogs.Find(session, c => c.Id == id && !c.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_ENUM_CATALOG_UNAVAILABLE");
            optionSets[id] = new(id, catalog.Options.Where(o => o.IsActive).Select(o => o.Code).ToHashSet(StringComparer.Ordinal));
        }
        DynamicFormNativeTableValues.Validate(form, form.PublishedSchemaHash, tableJson, submitting: false,
            optionSets: optionSets, enforceListUuidOnWrite: true, previousTableValuesJson: tableValuesJson);
        return (fieldJson, tableJson);
    }
    private static JsonNode? Scalar(string type, JsonElement? value)
    {
        if (value == null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (type == "NUMBER") return JsonNode.Parse(value.Value.GetString()!);
        if (type == "DATE_ONLY") return JsonValue.Create(AggregateTimeResolver.Date(value.Value.GetString()!).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        return JsonNode.Parse(value.Value.GetRawText());
    }
    private static bool Equivalent(JsonNode? before, JsonNode? after, string type)
    {
        if (before == null || after == null) return before == null && after == null;
        if (type == "NUMBER") return AggregateNumber.ParseJson(before.ToJsonString()).Compare(AggregateNumber.ParseJson(after.ToJsonString())) == 0;
        return JsonNode.DeepEquals(before, after);
    }
}
