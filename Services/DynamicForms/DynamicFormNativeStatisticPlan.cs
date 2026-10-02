using System.Text;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

/// <summary>L5a pure preparation. No storage, P8 mutation, defaults or job activation.</summary>
internal static class DynamicFormNativeStatisticPlan
{
    private static readonly string[] InputTypes = { "plainText", "number", "date", "fullDate", "boolean", "singleSelect", "multiSelect", "stringList" };
    private static readonly string[] Methods = { "COUNT", "DISTINCT_COUNT", "SUM", "AVG", "MIN", "MAX", "LATEST", "TRUE_COUNT", "FALSE_COUNT", "BUCKET_COUNT", "CONCAT", "STACK_ROWS", "STACK_COLUMNS" };

    internal sealed record CellBinding(string FieldId, string? RowId, DynamicFormNativeCellSpecDto Spec);
    internal sealed record GroupBinding(string? RowId, string? FieldId, bool RecordScoped,
        IReadOnlyList<CellBinding> Cells, int ExcludedCellCount);
    internal sealed record PreparedTarget(DynamicFormNativeStatisticPlanTargetDto Target, IReadOnlyList<GroupBinding> Groups);
    // Digest is plan content only, not a schema pin, P8 config hash or concurrency token.
    internal sealed record PreparedPlan(DynamicFormNativeStatisticPlanDto Plan, IReadOnlyList<PreparedTarget> Targets,
        string Json, string ContentDigest);

    internal static DynamicFormNativeStatisticPlanDto Read(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > 1_048_576) Fail("PLAN_BYTE_LIMIT");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        // Also rejects duplicate properties recursively before deserialization loses them.
        var plan = StatConfigCanonicalJson.DeserializeStrict<DynamicFormNativeStatisticPlanDto>(root);
        Shape(root);
        return plan;
    }

    internal static PreparedPlan Prepare(string raw, IReadOnlyList<DynamicFormNativeTableDto> tables,
        string sectionsJson, string fieldsJson, string blocksJson)
    {
        var plan = Read(raw);
        DynamicFormNativeTableDefinition.Validate(tables, sectionsJson, fieldsJson, blocksJson);
        var reserved = new HashSet<string>(tables.SelectMany(table => new[] { table.Id! }
            .Concat(table.Fields!.Select(field => field.Id!)).Concat(table.Rows!.Select(row => row.Id!))), StringComparer.Ordinal);
        foreach (var (json, key) in new[] { (sectionsJson, "id"), (fieldsJson, "id"), (blocksJson, "blockId") })
        {
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray()) reserved.Add(item.GetProperty(key).GetString()!);
        }
        var legacyOwners = tables.SelectMany(table => table.StatisticTargets!.Select(target => (target.Id!, TableId: table.Id!)))
            .ToDictionary(item => item.Item1, item => item.TableId, StringComparer.Ordinal);
        var resolvers = tables.ToDictionary(table => table.Id!, DynamicFormNativeTableDefinition.CompileCellTypes, StringComparer.Ordinal);
        var prepared = new List<PreparedTarget>();
        foreach (var target in plan.Targets!)
        {
            var table = tables.SingleOrDefault(table => table.Id == target.TableId);
            if (table is null || reserved.Contains(target.TargetId!)
                || legacyOwners.TryGetValue(target.TargetId!, out var oldTableId) && oldTableId != target.TableId)
                Fail("PLAN_TABLE_OR_TARGET_ID_INVALID");
            var groups = CompileSelection(table!, target, resolvers[table!.Id!]);
            var allTypes = groups.SelectMany(group => group.Cells).Select(cell => cell.Spec.Type!).ToHashSet(StringComparer.Ordinal);
            if (target.StatisticLabelCodes!.Count > 0 && allTypes.Count != 1) Fail("PLAN_LABEL_TYPE_MIXED");
            if (target.StatisticLabelCodes.Count > 0 && !SameChoiceOptions(groups.SelectMany(group => group.Cells))) Fail("PLAN_LABEL_OPTIONS_MIXED");
            foreach (var group in groups)
            {
                var types = group.Cells.Select(cell => cell.Spec.Type!).ToHashSet(StringComparer.Ordinal);
                if (types.Count == 0) Fail("PLAN_GROUP_EMPTY_AFTER_FILTER");
                if (types.Any(type => !InputTypes.Contains(type, StringComparer.Ordinal))) Fail("PLAN_INPUT_TYPE_UNSUPPORTED");
                var sameOptions = SameChoiceOptions(group.Cells);
                foreach (var operation in target.Operations!) ValidateOperation(operation, target, types, sameOptions);
            }
            prepared.Add(new PreparedTarget(target, groups));
        }
        var jsonResult = JsonSerializer.Serialize(plan, StatConfigCanonicalJson.StrictJsonOptions);
        return new PreparedPlan(plan, prepared, jsonResult, StatConfigCanonicalJson.HashObject(plan));
    }

    private static void Shape(JsonElement root)
    {
        Keys(root, "version", "targets");
        if (root.GetProperty("version").ValueKind != JsonValueKind.Number
            || !root.GetProperty("version").TryGetInt32(out var version) || version != 2) Fail("PLAN_VERSION_UNSUPPORTED");
        var targets = Array(root.GetProperty("targets"), 0, 30);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            Keys(target, "tableId", "targetId", "selector", "grouping", "input", "order", "operations", "statisticLabelCodes", "showInDetail", "showInTree");
            Id(target.GetProperty("tableId"));
            if (!targetIds.Add(Id(target.GetProperty("targetId")))) Fail("PLAN_TARGET_ID_DUPLICATE");
            var selector = target.GetProperty("selector");
            var kind = Discriminator(selector, "kind", "matrix", "records");
            Keys(selector, kind == "matrix" ? new[] { "kind", "fieldIds", "rowIds" } : new[] { "kind", "fieldIds", "records" });
            Ids(selector.GetProperty("fieldIds"), 200);
            if (kind == "matrix") Ids(selector.GetProperty("rowIds"), 200);
            else OneOf(selector.GetProperty("records"), "all");
            OneOf(target.GetProperty("grouping"), kind == "matrix"
                ? new[] { "selection", "row", "column", "cell" } : new[] { "selection", "column", "record" });
            var input = target.GetProperty("input");
            var mode = Discriminator(input, "mode", "all", "types");
            Keys(input, mode == "all" ? new[] { "mode" } : new[] { "mode", "types" });
            if (mode == "types")
                foreach (var type in Ids(input.GetProperty("types"), InputTypes.Length))
                    if (!InputTypes.Contains(type, StringComparer.Ordinal)) Fail("PLAN_TYPE_FILTER_UNSUPPORTED");
            var order = target.GetProperty("order");
            Keys(order, "cells", "reports", "records");
            OneOf(order.GetProperty("cells"), "rowMajor", "columnMajor");
            OneOf(order.GetProperty("reports"), "reportIdAsc", "sourceUpdatedAtAsc");
            OneOf(order.GetProperty("records"), "recordIdAsc");
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in Array(target.GetProperty("operations"), 1, 30))
            {
                Keys(operation, "operationId", "method", "options");
                if (!operationIds.Add(Id(operation.GetProperty("operationId")))) Fail("PLAN_OPERATION_ID_DUPLICATE");
                var method = OneOf(operation.GetProperty("method"), Methods);
                var options = operation.GetProperty("options");
                if (method == "CONCAT")
                {
                    Keys(options, "separator", "itemSeparator", "blankPolicy", "duplicatePolicy", "format", "choiceFormat");
                    if (options.GetProperty("separator").ValueKind != JsonValueKind.String
                        || options.GetProperty("itemSeparator").ValueKind != JsonValueKind.String) Fail("PLAN_SEPARATOR_LITERAL_REQUIRED");
                    OneOf(options.GetProperty("blankPolicy"), "keep", "skip");
                    OneOf(options.GetProperty("duplicatePolicy"), "keep", "distinct");
                    OneOf(options.GetProperty("format"), "text", "canonical");
                    OneOf(options.GetProperty("choiceFormat"), "code", "label");
                }
                else if (method == "BUCKET_COUNT") { Keys(options, "mode"); OneOf(options.GetProperty("mode"), "option", "date"); }
                else Keys(options);
            }
            foreach (var code in Array(target.GetProperty("statisticLabelCodes"), 0, 30))
                if (!labels.Add(Id(code))) Fail("PLAN_LABEL_DUPLICATE");
            if (target.GetProperty("showInDetail").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || target.GetProperty("showInTree").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("PLAN_DISPLAY_FLAGS_REQUIRED");
        }
    }

    private static IReadOnlyList<GroupBinding> CompileSelection(DynamicFormNativeTableDto table,
        DynamicFormNativeStatisticPlanTargetDto target, Func<string, string?, DynamicFormNativeCellSpecDto> resolve)
    {
        var selector = target.Selector!;
        if ((selector.Kind == "matrix") != (table.Layout == "matrix")
            || selector.FieldIds!.Any(id => !table.Fields!.Any(field => field.Id == id))
            || selector.Kind == "matrix" && selector.RowIds!.Any(id => !table.Rows!.Any(row => row.Id == id)))
            Fail("PLAN_SELECTOR_REFERENCE_INVALID");
        IEnumerable<string?> rows = selector.Kind == "matrix" ? selector.RowIds! : new string?[] { null };
        var addresses = target.Order!.Cells == "rowMajor"
            ? rows.SelectMany(row => selector.FieldIds!.Select(field => (Row: row, Field: field)))
            : selector.FieldIds!.SelectMany(field => rows.Select(row => (Row: row, Field: field)));
        // Preserve selector traversal order independently of dictionary enumeration semantics.
        var order = new List<(string? Row, string? Field)>();
        var cells = new Dictionary<(string? Row, string? Field), List<CellBinding>>();
        var excluded = new Dictionary<(string? Row, string? Field), int>();
        foreach (var address in addresses)
        {
            var key = (Row: target.Grouping is "row" or "cell" ? address.Row : null,
                Field: target.Grouping is "column" or "cell" ? address.Field : null);
            if (!cells.ContainsKey(key)) { order.Add(key); cells[key] = new(); excluded[key] = 0; }
            var spec = resolve(address.Field, address.Row);
            if (target.Input!.Mode == "types" && !target.Input.Types!.Contains(spec.Type!, StringComparer.Ordinal)) excluded[key]++;
            else
            {
                // Detach options from the mutable definition supplied by the caller.
                var detached = spec with { Options = spec.Options?.Select(option => option with { }).ToList() };
                cells[key].Add(new CellBinding(address.Field, address.Row, detached));
            }
        }
        return order.Select(key => new GroupBinding(key.Row, key.Field, target.Grouping == "record", cells[key], excluded[key])).ToArray();
    }

    private static bool SameChoiceOptions(IEnumerable<CellBinding> cells)
        => cells.Where(cell => cell.Spec.Type is "singleSelect" or "multiSelect")
            .Select(cell => StatConfigCanonicalJson.Canonicalize(cell.Spec.Options!.OrderBy(option => option.Code, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal).Take(2).Count() <= 1;

    private static void ValidateOperation(DynamicFormNativeStatisticOperationDto operation,
        DynamicFormNativeStatisticPlanTargetDto target, HashSet<string> types, bool sameOptions)
    {
        bool Only(params string[] allowed) => types.Count == 1 && allowed.Contains(types.Single(), StringComparer.Ordinal);
        var allowed = operation.Method switch
        {
            "COUNT" or "DISTINCT_COUNT" => true,
            "SUM" or "AVG" => Only("number"),
            "MIN" or "MAX" => Only("number", "date", "fullDate"),
            "LATEST" => types.Count == 1 && target.Order!.Reports == "sourceUpdatedAtAsc",
            "TRUE_COUNT" or "FALSE_COUNT" => Only("boolean"),
            "BUCKET_COUNT" => operation.Options.GetProperty("mode").GetString() == "date"
                ? Only("date", "fullDate") : Only("singleSelect", "multiSelect") && sameOptions,
            "CONCAT" => operation.Options.GetProperty("format").GetString() == "canonical"
                || types.All(type => type is "plainText" or "stringList"),
            "STACK_ROWS" or "STACK_COLUMNS" => target.Grouping == "selection" && target.Input!.Mode == "all",
            _ => false
        };
        if (!allowed) Fail("PLAN_OPERATION_TYPE_OR_GROUP_MISMATCH");
    }

    private static void Keys(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail("PLAN_OBJECT_REQUIRED");
        var keys = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Length != required.Length || required.Any(key => !keys.Contains(key, StringComparer.Ordinal))) Fail("PLAN_KEYS_REQUIRED_OR_UNKNOWN");
    }
    private static JsonElement[] Array(JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max) Fail("PLAN_ARRAY_LIMIT");
        return value.EnumerateArray().ToArray();
    }
    private static string Id(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) Fail("PLAN_ID_REQUIRED");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim()) Fail("PLAN_ID_REQUIRED");
        return text!;
    }
    private static string[] Ids(JsonElement value, int max)
    {
        var ids = Array(value, 1, max).Select(Id).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) Fail("PLAN_SELECTOR_ID_DUPLICATE");
        return ids;
    }
    private static string Discriminator(JsonElement value, string key, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out _)) Fail("PLAN_DISCRIMINATOR_REQUIRED");
        return OneOf(value.GetProperty(key), allowed);
    }
    private static string OneOf(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.String || !allowed.Contains(value.GetString(), StringComparer.Ordinal)) Fail("PLAN_ENUM_UNSUPPORTED");
        return value.GetString()!;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string reason) => throw AppExceptionFactory.BadRequest(AppErrorCode.STAT_CONFIG_SCHEMA_INVALID, new { path = "$.nativeStatisticPlan", reason });
}
