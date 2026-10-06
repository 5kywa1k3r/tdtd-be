using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateNativePayloadAdapter
{
    // Only a report's untouched target draft has no native payload yet. An
    // Approved source or a previously saved/malformed payload stays strict.
    internal static bool IsUninitializedTarget(WorkReportPayloadSnapshot payload) =>
        payload.PayloadRevision <= 1 && payload.Values1DJson.Trim() == "[]"
        && (string.IsNullOrWhiteSpace(payload.FieldValuesJson) || payload.FieldValuesJson.Trim() is "{}" or "{\"values\":{}}")
        && (string.IsNullOrWhiteSpace(payload.TableValuesJson) || payload.TableValuesJson.Trim() == "{}");
    internal static JsonArray ListRecords(DynamicFormNativeTableDto definition, AggregateListValue? list,
        Func<AggregateListRow, string> identity)
    {
        if (definition.Presentation?.Kind != "LIST" || definition.Layout == "matrix") throw new AggregatePreviewException("AGG_LIST_TARGET_SCHEMA");
        var records = new JsonArray(); var identities = new HashSet<string>(StringComparer.Ordinal);
        var resolve = DynamicFormNativeTableDefinition.CompileCellTypes(definition);
        foreach (var row in list?.Records ?? [])
        {
            var id = identity(row);
            if (!identities.Add(id)) throw new AggregatePreviewException("AGG_LIST_ID_COLLISION");
            var cells = new JsonObject();
            foreach (var field in definition.Fields!)
            {
                if (!row.Cells.TryGetValue(field.Id!, out var cell)) throw new AggregatePreviewException("AGG_LIST_TARGET_SCHEMA");
                if (cell.Value.State == "BLANK") continue;
                if (cell.Value.State != "VALUE") throw new AggregatePreviewException("AGG_LIST_SOURCE_VALUE_INVALID");
                var wire = AggregatePreviewService.ToWire(cell.Value)!.Value;
                var value = cell.Value.Type == "NUMBER" ? JsonNode.Parse(wire.GetString()!)
                    : cell.Value.Type == "DATE_ONLY" ? JsonValue.Create(AggregateTimeResolver.Date(wire.GetString()!).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
                    : JsonNode.Parse(wire.GetRawText());
                cells[field.Id!] = new JsonObject { ["type"] = resolve(field.Id!, null).Type, ["state"] = "value", ["value"] = value };
            }
            records.Add(new JsonObject { ["recordId"] = id, ["cells"] = cells });
        }
        return records;
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static AggregateSchema Schema(DynamicFormTemplate template)
    {
        // Validate immutable published snapshot against live structure, never mutate/backfill.
        DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template);
        var fields = JsonSerializer.Deserialize<List<DynamicFormFieldDto>>(template.FieldsJson, Json) ?? [];
        var members = fields.Where(f => f.Type != "evidence").ToDictionary(f => f.Id!, f => new AggregateMember(f.Id!, FieldType(f.Type),
            AllowedChoiceCodes: Type(f.Type) is "CHOICE_ONE" or "CHOICE_MANY"
                ? (f.ValueSource?.Options ?? f.Options ?? []).Select(o => o.Code!).ToArray() : null));
        foreach (var table in DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson) ?? [])
        {
            var columns = table.Fields!.Select(f => f.Id!).ToArray();
            var rows = table.Rows!.Select(r => r.Id!).ToArray();
            var resolve = DynamicFormNativeTableDefinition.CompileCellTypes(table);
            var cellTypes = (table.Layout == "matrix" ? rows : new[] { "" }).Select(row => (IReadOnlyList<string>)
                columns.Select(column => Type(resolve(column, table.Layout == "matrix" ? row : null).Type)).ToArray()).ToArray();
            if (table.Presentation?.Kind == "LIST")
            {
                members.Add(table.Id!, new(table.Id!, "LIST", List: new(columns.Select((id, c) => new AggregateListField(id, cellTypes[0][c]) {
                    AllowedChoiceCodes = cellTypes[0][c] is "CHOICE_ONE" or "CHOICE_MANY" ? (resolve(id, null).Options ?? []).Select(o => o.Code!).ToArray() : null
                }).ToArray())));
                continue;
            }
            members.Add(table.Id!, new(table.Id!, "TABLE", new(template.PublishedSchemaHash + ":" + table.Id, table.Layout!, columns, rows, cellTypes)));
        }
        return new(new(template.Id, template.FamilyId!, template.VersionNo, template.PublishedSchemaHash!), members);
    }

    internal static IReadOnlyDictionary<string, AggregateValue> Read(DynamicFormTemplate template, WorkReportPayloadSnapshot payload,
        AggregateSchema schema, AggregateSourceHeader? header, IReadOnlyDictionary<string, AggregateTable>? externalTables = null,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? listOptions = null)
    {
        if (payload.PayloadSizeBytes > 16_777_216) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        using var fieldsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload.FieldValuesJson) ? "{}" : payload.FieldValuesJson);
        var fields = fieldsDoc.RootElement.TryGetProperty("values", out var envelope) ? envelope : fieldsDoc.RootElement;
        if (fields.ValueKind != JsonValueKind.Object || fields.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
            throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
        var definitions = JsonSerializer.Deserialize<List<DynamicFormFieldDto>>(template.FieldsJson, Json) ?? [];
        var values = new Dictionary<string, AggregateValue>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!schema.Members.TryGetValue(definition.Id!, out var member)) continue;
            if (member.Type == "UNSUPPORTED") continue;
            var found = fields.TryGetProperty(definition.Id!, out var value);
            if (!string.IsNullOrEmpty(definition.Key) && definition.Key != definition.Id && fields.TryGetProperty(definition.Key, out var alias))
            {
                if (found) throw new AggregatePreviewException("AGG_SOURCE_VALUE_AMBIGUOUS");
                found = true; value = alias;
            }
            try { values[definition.Id!] = (found ? definition.Type == "stringList" ? StringListBlock(value) : Scalar(value, member.Type) : AggregateValue.Blank(member.Type))
                with { TextFormat = definition.Type == "richText" ? "RICH_HTML" : definition.Type == "stringList" ? AggregateTextPolicy.StringListBlock : null }; }
            catch (AggregatePreviewException) when (header == null)
            {
                // Previous target data can be an invalid draft. Preserve it for the diff;
                // it must not prevent calculating unrelated targets or become a fake zero.
                values[definition.Id!] = new(member.Type, "INVALID", Text: value.GetRawText());
            }
        }
        if (!schema.Members.Values.Any(m => m.Table != null || m.List != null)) return values;
        if (header == null && IsUninitializedTarget(payload))
        {
            foreach (var member in schema.Members.Values.Where(m => m.Table != null || m.List != null))
                values[member.Id] = AggregateValue.NoResult(member.Type);
            return values;
        }
        DynamicFormNativeTableValues.Validate(template, schema.Pin.SchemaHash, payload.TableValuesJson, submitting: false, optionSets: listOptions);
        using var tablesDoc = JsonDocument.Parse(payload.TableValuesJson!);
        var native = tablesDoc.RootElement.GetProperty("nativeTables").GetProperty("tables");
        foreach (var tableValue in native.EnumerateArray())
        {
            var id = tableValue.GetProperty("tableId").GetString()!;
            if (!schema.Members.ContainsKey(id)) continue;
            if (schema.Members[id].List is { } listSchema)
            {
                var items = new List<AggregateListRow>();
                foreach (var record in tableValue.GetProperty("records").EnumerateArray())
                {
                    var recordId = record.GetProperty("recordId").GetString()!;
                    var cells = record.GetProperty("cells");
                    var output = new Dictionary<string, AggregateCell>(StringComparer.Ordinal);
                    foreach (var field in listSchema.Fields)
                    {
                        var cell = AggregateValue.Blank(field.Type);
                        if (cells.TryGetProperty(field.Id, out var stored))
                        {
                            if (stored.GetProperty("state").GetString() != "value") throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
                            cell = AggregateListChoicePolicy.Normalize(Scalar(stored.GetProperty("value"), field.Type));
                        }
                        output.Add(field.Id, new(cell, header == null ? [] : [new(header.Pin.ReportId, header.UnitId,
                            header.OccurrenceKey, id, RecordId: recordId, FieldId: field.Id)]));
                    }
                    var origin = new AggregateListOrigin(header?.Pin, header?.UnitId ?? "", header?.OccurrenceKey ?? "", id, recordId, "") { Form = schema.Pin };
                    items.Add(new(AggregateDigest.Of(new { ReportId = header?.Pin.ReportId, ListId = id, RecordId = recordId }), origin, output));
                }
                values[id] = new("LIST", "VALUE") { List = new(listSchema, items) };
                continue;
            }
            var definition = schema.Members[id].Table!;
            if (tableValue.TryGetProperty("contentRef", out var external))
            {
                var reference = external.Deserialize<AggregateContentReference>(Json)
                    ?? throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
                if (header == null) values[id] = new("TABLE", "VALUE") { ContentReference = reference };
                else
                {
                    if (externalTables == null || !externalTables.TryGetValue(id, out var content))
                        throw new AggregatePreviewException("AGG_CONTENT_UNAVAILABLE");
                    // A parent's source is this submitted report, never its private upstream mapping.
                    var wrapped = content.Rows.Select((row, r) => (IReadOnlyList<AggregateCell>)row.Select((cell, c) =>
                        new AggregateCell(cell.Value, [new(header.Pin.ReportId, header.UnitId, header.OccurrenceKey, id, r + 1, c + 1)])).ToArray()).ToArray();
                    values[id] = new("TABLE", "VALUE", Table: new(definition, wrapped, Enumerable.Repeat(header.UnitId, wrapped.Length).ToArray()));
                }
                continue;
            }
            var matrix = definition.Layout == "matrix";
            var records = tableValue.GetProperty(matrix ? "rows" : "records").EnumerateArray().ToArray();
            if (matrix) records = definition.Rows.Select(rowId => records.Single(r => r.GetProperty("rowId").GetString() == rowId)).ToArray();
            var rows = new List<IReadOnlyList<AggregateCell>>();
            for (var r = 0; r < records.Length; r++)
            {
                var cells = records[r].GetProperty("cells"); var output = new List<AggregateCell>();
                for (var c = 0; c < definition.Columns.Count; c++)
                {
                    var type = definition.CellTypes[matrix ? r : 0][c];
                    if (type == "UNSUPPORTED") throw new AggregatePreviewException("AGG_TABLE_CELL_TYPE_UNSUPPORTED");
                    var cell = AggregateValue.Blank(type);
                    if (cells.TryGetProperty(definition.Columns[c], out var stored))
                    {
                        if (stored.GetProperty("state").GetString() != "value") throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
                        cell = Scalar(stored.GetProperty("value"), type);
                    }
                    output.Add(new(cell, header == null ? [] : [new(header.Pin.ReportId, header.UnitId, header.OccurrenceKey, id, r + 1, c + 1)]));
                }
                rows.Add(output);
            }
            var actual = definition with { Rows = Enumerable.Range(1, rows.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(),
                CellTypes = rows.Select(r => (IReadOnlyList<string>)r.Select(c => c.Value.Type).ToArray()).ToArray() };
            values[id] = new("TABLE", "VALUE", Table: new(actual, rows, Enumerable.Repeat(header?.UnitId ?? "", rows.Count).ToArray()));
        }
        return values;
    }
    internal static IEnumerable<string> ListCatalogIds(DynamicFormTemplate template)
        => (DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson) ?? [])
            .Where(t => t.Presentation?.Kind == "LIST").SelectMany(t => {
                var resolve = DynamicFormNativeTableDefinition.CompileCellTypes(t);
                return t.Fields!.Select(f => resolve(f.Id!, null).ValueSource)
                    .Where(s => s?.SourceType == "ENUM_CATALOG").Select(s => s!.CatalogId!);
            }).Distinct(StringComparer.Ordinal);
    // Field-only presentation adapter. Native Table/List cell contracts are unchanged.
    internal static string FieldType(string? type) => type == "stringList" ? "TEXT" : Type(type);
    private static AggregateValue StringListBlock(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return AggregateValue.Blank("TEXT");
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
        var items = value.EnumerateArray().Select(v => v.GetString()!).ToArray();
        if (items.Any(s => string.IsNullOrWhiteSpace(s) || s != s.Trim()) || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
        return items.Length == 0 ? AggregateValue.Blank("TEXT") : new("TEXT", "VALUE", Text: string.Join("\n", items));
    }
    private static string Type(string? type) => type switch
    {
        "number" => "NUMBER", "longText" or "richText" or "plainText" => "TEXT",
        "shortText" or "singleSelect" => "CHOICE_ONE", "multiSelect" => "CHOICE_MANY",
        "boolean" => "BOOLEAN", "fullDate" => "DATE_ONLY", "date" => "DATE_PARTIAL", _ => "UNSUPPORTED"
    };
    private static AggregateValue Scalar(JsonElement value, string type)
    {
        if (value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString() == "")) return AggregateValue.Blank(type);
        if (type == "NUMBER" && value.ValueKind == JsonValueKind.Number)
        {
            return AggregateValue.Numeric(AggregateNumber.ParseJson(value.GetRawText()));
        }
        if (type == "BOOLEAN" && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return new(type, "VALUE", Boolean: value.GetBoolean());
        if (type == "TEXT" && value.ValueKind == JsonValueKind.String) return new(type, "VALUE", Text: value.GetString());
        if (type == "CHOICE_ONE" && value.ValueKind == JsonValueKind.String) return new(type, "VALUE", Text: value.GetString());
        if (type == "CHOICE_MANY" && value.ValueKind == JsonValueKind.Array)
        {
            if (value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            var codes = value.EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Length) throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            return new(type, "VALUE", Choices: codes);
        }
        if (type == "DATE_PARTIAL" && value.ValueKind == JsonValueKind.String)
            return new(type, "VALUE", Text: AggregatePartialDate.Parse(value.GetString()!).Text);
        if (type == "DATE_ONLY" && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!DateOnly.TryParseExact(text, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new AggregatePreviewException("AGG_DATE_PRECISION_UNRESOLVED"); // month/year is not a full-day window
            return new(type, "VALUE", Text: date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
    }
}
