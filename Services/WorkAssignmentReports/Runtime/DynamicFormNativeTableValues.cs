using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

/// <summary>Native values remain inside the existing report payload/CAS/receipt.
/// Validation never flattens them into Excel Values1D or rewrites raw errors.</summary>
internal static class DynamicFormNativeTableValues
{
    internal const int Version = 1;
    internal const int MaximumRecordsPerTable = 200;
    internal const int MaximumCells = 40_000;
    internal const int MaximumBytes = 1_048_576;

    internal static void Validate(DynamicFormTemplate? form, string? pinnedSchemaHash,
        string? tableValuesJson, bool submitting,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? optionSets = null,
        bool enforceListUuidOnWrite = false, string? previousTableValuesJson = null,
        IReadOnlySet<string>? allowedSystemUnitIds = null)
    {
        try
        {
            var legacyListIds = enforceListUuidOnWrite ? ReadExistingRecordIds(previousTableValuesJson) : null;
            ValidateCore(form, pinnedSchemaHash, tableValuesJson, submitting,
                optionSets ?? new Dictionary<string, RuntimeEnumOptionSet>(), legacyListIds, allowedSystemUnitIds);
        }
        catch (JsonException) { Fail("NATIVE_VALUES_JSON_INVALID", "tableValuesJson"); }
    }

    private static void ValidateCore(DynamicFormTemplate? form, string? pinnedSchemaHash,
        string? tableValuesJson, bool submitting, IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets,
        IReadOnlySet<(string TableId, string RecordId)>? existingRecordIds,
        IReadOnlySet<string>? allowedSystemUnitIds)
    {
        using var document = string.IsNullOrWhiteSpace(tableValuesJson) ? null : JsonDocument.Parse(tableValuesJson);
        var root = document?.RootElement;
        JsonElement envelope = default;
        var hasNative = root is { ValueKind: JsonValueKind.Object } && root.Value.TryGetProperty("nativeTables", out envelope);
        if (hasNative && root!.Value.EnumerateObject().Count(p => p.Name == "nativeTables") != 1)
            Fail("NATIVE_VALUE_PROPERTY_UNKNOWN_OR_DUPLICATE", "nativeTables");
        if (form is null || !DynamicFormNativeTableDefinition.IsNative(form))
        {
            if (hasNative) Fail("NATIVE_VALUES_WITHOUT_DEFINITION", "nativeTables");
            return;
        }
        if (!hasNative) Fail("NATIVE_VALUES_EXPLICIT_INPUT_REQUIRED", "nativeTables");
        if (Encoding.UTF8.GetByteCount(tableValuesJson!) > MaximumBytes)
            Fail("NATIVE_VALUES_BYTE_LIMIT", "nativeTables");
        if (pinnedSchemaHash != form.PublishedSchemaHash) Fail("NATIVE_VALUES_SCHEMA_PIN_MISMATCH", "nativeTables.schemaHash");
        DynamicFormNativeTableDefinition.ValidateStored(form);
        var definitions = DynamicFormNativeTableDefinition.ReadStored(form.NativeTablesVersion, form.TablesJson)!;
        ValidateEnvelope(definitions, pinnedSchemaHash, envelope, submitting, optionSets, existingRecordIds, allowedSystemUnitIds);
    }

    private static IReadOnlySet<(string TableId, string RecordId)> ReadExistingRecordIds(string? previousTableValuesJson)
    {
        var ids = new HashSet<(string TableId, string RecordId)>();
        if (string.IsNullOrWhiteSpace(previousTableValuesJson)) return ids;
        using var previous = JsonDocument.Parse(previousTableValuesJson);
        if (previous.RootElement.ValueKind != JsonValueKind.Object
            || !previous.RootElement.TryGetProperty("nativeTables", out var native)
            || native.ValueKind != JsonValueKind.Object
            || !native.TryGetProperty("tables", out var tables)
            || tables.ValueKind != JsonValueKind.Array) return ids;
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object
                || !table.TryGetProperty("tableId", out var tableId) || tableId.ValueKind != JsonValueKind.String
                || !table.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array) continue;
            foreach (var record in records.EnumerateArray())
                if (record.ValueKind == JsonValueKind.Object
                    && record.TryGetProperty("recordId", out var recordId) && recordId.ValueKind == JsonValueKind.String)
                    ids.Add((tableId.GetString()!, recordId.GetString()!));
        }
        return ids;
    }

    // Projection callers supply definitions from the validated, pinned section snapshots.
    internal static void ValidateEnvelope(IReadOnlyList<DynamicFormNativeTableDto> definitions, string? pinnedSchemaHash,
        JsonElement envelope, bool submitting,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet>? optionSets = null,
        IReadOnlySet<(string TableId, string RecordId)>? existingRecordIds = null,
        IReadOnlySet<string>? allowedSystemUnitIds = null)
    {
        optionSets ??= new Dictionary<string, RuntimeEnumOptionSet>();
        Object(envelope, "nativeTables", "version", "schemaHash", "tables");
        if (!envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v) || v != Version)
            Fail("NATIVE_VALUES_VERSION_UNSUPPORTED", "nativeTables.version");
        var hash = Text(envelope, "schemaHash", "nativeTables.schemaHash");
        if (string.IsNullOrWhiteSpace(pinnedSchemaHash) || hash != pinnedSchemaHash)
            Fail("NATIVE_VALUES_SCHEMA_PIN_MISMATCH", "nativeTables.schemaHash");
        var tables = Array(envelope, "tables", "nativeTables.tables");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cells = 0;
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object) Fail("NATIVE_VALUE_OBJECT_REQUIRED", "nativeTables.tables");
            var id = Text(table, "tableId", "nativeTables.tables.tableId");
            var definition = definitions.SingleOrDefault(t => t.Id == id);
            if (definition is null || !seen.Add(id)) Fail("NATIVE_TABLE_UNKNOWN_OR_DUPLICATE", id);
            // D1: one value set per table/report. Matrix row identity comes from
            // the pinned definition; there is no instanceId or synthetic recordId.
            var matrix = definition!.Layout == "matrix";
            var collection = matrix ? "rows" : "records";
            var key = matrix ? "rowId" : "recordId";
            Object(table, "nativeTables.tables", "tableId", collection, "contentRef");
            var records = Array(table, collection, id + "." + collection);
            if (table.TryGetProperty("contentRef", out var contentRef))
            {
                if (matrix || definition.Presentation?.Kind == "LIST" || definition.Layout != "vertical" || definition.Fields!.Count != 2 || records.GetArrayLength() != 0)
                    Fail("NATIVE_CONTENT_TABLE_SHAPE", id);
                Object(contentRef, id + ".contentRef", "kind", "id", "hash", "rowCount", "blankRows", "maxContentLength", "maxUnitLength");
                var reference = contentRef.Deserialize<tdtd_be.Services.AggregateMapping.AggregateContentReference>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (reference == null || reference.Kind != "REPORT_CONTENT_TABLE_V1" || string.IsNullOrEmpty(reference.Id) || reference.Id != reference.Hash
                    || reference.Id.Length != 64 || !reference.Id.All(Uri.IsHexDigit) || reference.RowCount is < 0 or > 10_000
                    || reference.BlankRows < 0 || reference.BlankRows > reference.RowCount || reference.MaxContentLength < 0 || reference.MaxUnitLength < 0)
                    Fail("NATIVE_CONTENT_TABLE_REFERENCE", id);
                var types = DynamicFormNativeTableDefinition.CompileCellTypes(definition);
                for (var column = 0; column < 2; column++)
                {
                    var spec = types(definition.Fields[column].Id!, null);
                    if (spec.Type != "plainText") Fail("NATIVE_CONTENT_TABLE_TYPE", id);
                    if (spec.MaxLength.HasValue && (column == 0 ? reference!.MaxUnitLength : reference!.MaxContentLength) > spec.MaxLength.Value)
                        Fail("NATIVE_VALUE_MAX_LENGTH", id);
                    if (submitting && spec.Required == true && column == 1 && reference!.BlankRows > 0) Fail("NATIVE_VALUE_REQUIRED", id);
                }
                continue; // Authoritative writer also verifies this reference against the transaction's binding.
            }
            if (!matrix && records.GetArrayLength() > MaximumRecordsPerTable) Fail("NATIVE_RECORD_LIMIT_200", id);
            if (!matrix && definition.Presentation?.Kind == "LIST")
            {
                var count = records.GetArrayLength();
                if (count > definition.ItemConstraints!.MaxItems
                    || submitting && count < definition.ItemConstraints.MinItems)
                    Fail("NATIVE_LIST_ITEM_COUNT_INVALID", id);
            }
            var rowIds = new HashSet<string>(StringComparer.Ordinal);
            var definedRows = definition.Rows!.Select(row => row.Id!).ToHashSet(StringComparer.Ordinal);
            var fieldIds = definition.Fields!.Select(field => field.Id!).ToArray();
            var resolve = DynamicFormNativeTableDefinition.CompileCellTypes(definition);
            foreach (var record in records.EnumerateArray())
            {
                Object(record, id + "." + collection, key, "cells");
                var rowId = Text(record, key, id + "." + key);
                if (!rowIds.Add(rowId)) Fail(matrix ? "NATIVE_MATRIX_ROW_DUPLICATE" : "NATIVE_RECORD_ID_DUPLICATE", id + "." + rowId);
                if (matrix && !definedRows.Contains(rowId)) Fail("NATIVE_MATRIX_ROW_UNKNOWN", id + "." + rowId);
                if (!matrix && definition.Presentation?.Kind == "LIST" && existingRecordIds is not null
                    && !Guid.TryParseExact(rowId, "D", out _) && !existingRecordIds.Contains((id, rowId)))
                    Fail("NATIVE_LIST_ITEM_UUID_REQUIRED", id + "." + rowId);
                if (!record.TryGetProperty("cells", out var values)) Fail("NATIVE_CELLS_REQUIRED", id + "." + rowId);
                Object(values, id + "." + rowId, fieldIds);
                cells += fieldIds.Length;
                if (cells > MaximumCells) Fail("NATIVE_CELL_LIMIT_40000", id);
                foreach (var fieldId in fieldIds)
                {
                    var spec = resolve(fieldId, matrix ? rowId : null);
                    var path = id + "." + rowId + "." + fieldId;
                    if (!values.TryGetProperty(fieldId, out var cell))
                    {
                        if (submitting && spec.Required == true) Fail("NATIVE_VALUE_REQUIRED", path);
                        continue;
                    }
                    ValidateCell(cell, spec, submitting, path, optionSets, allowedSystemUnitIds);
                }
            }
            if (matrix && rowIds.Count != definedRows.Count) Fail("NATIVE_MATRIX_ROW_SET_INCOMPLETE", id);
        }
        // Explicit empty records clears a list; matrix keeps every defined row,
        // with cells={} for an unentered row. Omission is not a delete command.
        if (seen.Count != definitions.Count) Fail("NATIVE_TABLE_SET_INCOMPLETE", "nativeTables.tables");
    }

    private static void ValidateCell(JsonElement cell, DynamicFormNativeCellSpecDto spec, bool submitting, string path,
        IReadOnlyDictionary<string, RuntimeEnumOptionSet> optionSets, IReadOnlySet<string>? allowedSystemUnitIds)
    {
        Object(cell, path, "type", "state", "value", "raw", "code");
        var type = Text(cell, "type", path);
        var state = Text(cell, "state", path);
        if (type != spec.Type) Fail("NATIVE_CELL_TYPE_CHANGED", path);
        if (state == "error")
        {
            JsonElement raw = default;
            if (cell.TryGetProperty("value", out _) || !cell.TryGetProperty("raw", out raw)
                || raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)) Fail("NATIVE_ERROR_SHAPE_INVALID", path);
            if (raw.ValueKind == JsonValueKind.Array && raw.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                Fail("NATIVE_ERROR_RAW_INVALID", path);
            _ = Text(cell, "code", path);
            if (submitting) Fail("NATIVE_VALUE_ERROR_UNRESOLVED", path);
            return;
        }
        JsonElement value = default;
        if (state != "value" || cell.TryGetProperty("raw", out _) || cell.TryGetProperty("code", out _)
            || !cell.TryGetProperty("value", out value)) Fail("NATIVE_VALUE_SHAPE_INVALID", path);
        if (spec.MaxLength.HasValue && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > spec.MaxLength)
            Fail("NATIVE_VALUE_MAX_LENGTH", path);
        if (spec.MinLength.HasValue && value.ValueKind == JsonValueKind.String && value.GetString()!.Length < spec.MinLength)
            Fail("NATIVE_VALUE_MIN_LENGTH", path);
        if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)
            || Math.Truncate(number) == number && Math.Abs(number) > 9_007_199_254_740_991
            || !value.TryGetDecimal(out var decimalNumber) || !ExactNumber(decimalNumber, number)
            || !SupportedNumberLiteral(value.GetRawText())))
            Fail("NATIVE_NUMBER_RANGE", path);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var bounded))
        {
            if (spec.Minimum.HasValue && bounded < spec.Minimum || spec.Maximum.HasValue && bounded > spec.Maximum)
                Fail("NATIVE_NUMBER_CONSTRAINT", path);
            if (spec.IntegerOnly == true && decimal.Truncate(bounded) != bounded) Fail("NATIVE_NUMBER_INTEGER_REQUIRED", path);
        }
        if (spec.Type == "fullDate" && value.ValueKind == JsonValueKind.String)
        {
            var dateText = value.GetString();
            if (dateText is not null && (!DateOnly.TryParseExact(dateText, "dd/MM/yyyy", out var date)
                || spec.MinDate is not null && (!DateOnly.TryParseExact(spec.MinDate, "yyyy-MM-dd", out var minDate) || date < minDate)
                || spec.MaxDate is not null && (!DateOnly.TryParseExact(spec.MaxDate, "yyyy-MM-dd", out var maxDate) || date > maxDate)))
                Fail("NATIVE_DATE_CONSTRAINT", path);
        }
        if (spec.Type == "multiSelect" && value.ValueKind == JsonValueKind.Array)
        {
            var selected = value.GetArrayLength();
            if (spec.MinSelected.HasValue && selected < spec.MinSelected || spec.MaxSelected.HasValue && selected > spec.MaxSelected)
                Fail("NATIVE_SELECTION_CONSTRAINT", path);
        }
        var catalogId = spec.ValueSource?.SourceType == "ENUM_CATALOG" ? spec.ValueSource.CatalogId : null;
        var allowed = spec.Options?.Select(option => option.Code!).ToArray();
        if (!string.IsNullOrWhiteSpace(catalogId))
        {
            if (!optionSets.TryGetValue(catalogId, out var catalog)) Fail("NATIVE_ENUM_CATALOG_UNAVAILABLE", path);
            allowed = catalog.Codes.ToArray();
        }
        if (spec.ValueSource?.SourceType == "SYSTEM_UNIT")
        {
            var selected = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : [];
            // Stored report projections are read after the authoritative writer validated membership.
            // A write always supplies the active-unit set, so stale/deleted IDs cannot enter a new payload.
            allowed = allowedSystemUnitIds?.ToArray() ?? selected.Where(x => ObjectId.TryParse(x, out _)).ToArray();
        }
        if (spec.ValueSource?.SourceType == "SYSTEM_LOCALITY")
            allowed = SystemLocalityCatalog.Official.Rows.Select(x => x.Code).ToArray();
        var field = new ResolvedDynamicFormRuntimeFieldDefinition("cell", type == "plainText" ? "longText" : type,
            Required: spec.Required == true, AllowedChoiceCodes: allowed);
        try
        {
            var input = "{\"cell\":" + value.GetRawText() + "}";
            var result = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(input, new[] { field }, submitting);
            using var canonical = JsonDocument.Parse(result.CanonicalValuesJson);
            // Native choice codes/text are identities. Do not silently normalize
            // a stored value or sanitize it to a different value while validating.
            if (!Equivalent(value, canonical.RootElement.GetProperty("cell"))) Fail("NATIVE_VALUE_NOT_CANONICAL", path);
        }
        catch (DynamicFormRuntimeFieldValidationException exception) { Fail(exception.Reason, path); }
    }

    private static bool ExactNumber(decimal value, double number)
        => decimal.TryParse(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var roundTrip)
           && roundTrip == value;

    private static bool SupportedNumberLiteral(string text)
    {
        var parts = text.ToLowerInvariant().Split('e');
        var exponent = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out exponent)) return false;
        var point = parts[0].IndexOf('.');
        var scale = (point < 0 ? 0L : parts[0].Length - point - 1L) - exponent;
        var significant = parts[0].Replace(".", "").TrimStart('-', '+', '0').TrimEnd('0').Length;
        return scale <= 28 && significant <= 17;
    }

    private static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Number => left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second)),
            JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False => true,
            _ => false
        };
    }

    private static void Object(JsonElement value, string path, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail("NATIVE_VALUE_OBJECT_REQUIRED", path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                Fail("NATIVE_VALUE_PROPERTY_UNKNOWN_OR_DUPLICATE", path + "." + property.Name);
    }
    private static JsonElement Array(JsonElement value, string name, string path)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            Fail("NATIVE_VALUE_ARRAY_REQUIRED", path);
        return array;
    }
    private static string Text(JsonElement value, string name, string path)
    {
        if (!value.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(text.GetString()) || text.GetString() != text.GetString()!.Trim())
            Fail("NATIVE_VALUE_ID_OR_TEXT_INVALID", path);
        return text.GetString()!;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string reason, string path)
        => throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID, new { reason, path, scope = "nativeTables" });
}
