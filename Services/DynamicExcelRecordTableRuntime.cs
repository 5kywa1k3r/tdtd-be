using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Common.Errors;

namespace tdtd_be.Services;

public static class DynamicExcelRecordTableRuntime
{
    public sealed record ColumnSpec(
        string Key,
        string Label,
        string DataType,
        bool Required,
        bool IsCalculated,
        JsonNode? Expression);

    public sealed record RuleSpec(string Key, string Message, JsonNode Condition, JsonNode? When);

    public sealed record TableSpec(
        string Orientation,
        List<ColumnSpec> Columns,
        List<ColumnSpec> CalculatedOutputs,
        List<RuleSpec> Rules);

    public sealed record RecordRow(string RowKey, int RowIndex, Dictionary<string, object?> Values);

    public static TableSpec ParseSpec(string? recordTableSpecJson)
    {
        DynamicExcelRecordTableContractValidator.Validate(recordTableSpecJson);

        var root = JsonNode.Parse(recordTableSpecJson!) as JsonObject
            ?? throw Validation("Cấu hình bảng dữ liệu phát sinh phải là JSON object.");

        var orientation = ReadString(root, "orientation")?.Trim().ToUpperInvariant() == "COLUMNS"
            ? "COLUMNS"
            : "ROWS";

        var columns = ReadObjectArray(root, "columns")
            .Select(item => new ColumnSpec(
                RequireKey(item, "columns[].key"),
                ReadString(item, "label") ?? RequireKey(item, "columns[].key"),
                NormalizeDataType(ReadString(item, "dataType") ?? ReadString(item, "type")),
                ReadBool(item, "required") ?? false,
                false,
                null))
            .ToList();

        var outputs = new[] { "calculatedColumns", "calculatedRows", "aggregateColumns", "aggregateRows" }
            .SelectMany(field => ReadObjectArray(root, field))
            .Select(item => new ColumnSpec(
                RequireKey(item, "calculated.key"),
                ReadString(item, "label") ?? RequireKey(item, "calculated.key"),
                NormalizeDataType(ReadString(item, "dataType") ?? ReadString(item, "type")),
                false,
                true,
                CloneNode(ReadExpression(item)) ?? throw Validation(
                    "Cột/hàng tính toán phải có expression.",
                    new { key = RequireKey(item, "calculated.key") })))
            .ToList();

        var rules = new[] { "validationRules", "rowRules", "columnRules" }
            .SelectMany(field => ReadObjectArray(root, field))
            .Select(item => new RuleSpec(
                ReadString(item, "key") ?? ReadString(item, "id") ?? $"rule_{Guid.NewGuid():N}",
                ReadString(item, "message") ?? "Dữ liệu dòng không thỏa điều kiện kiểm tra.",
                CloneNode(ReadExpression(item)) ?? throw Validation("Điều kiện kiểm tra dữ liệu phải có expression/condition."),
                CloneNode(ReadNode(item, "when"))))
            .ToList();

        return new TableSpec(orientation, columns, outputs, rules);
    }

    public static void ValidateTableValues(
        string? tableValuesJson,
        string? recordTableSpecJson,
        string? dynamicExcelTemplateId,
        string? reportId = null)
    {
        var spec = ParseSpec(recordTableSpecJson);
        var rows = ExtractRows(tableValuesJson, spec, dynamicExcelTemplateId);
        var collectedKeys = spec.Columns.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var calculatedKeys = spec.CalculatedOutputs.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var unknownKeys = row.Values.Keys
                .Where(key => !collectedKeys.Contains(key))
                .Take(20)
                .ToList();
            if (unknownKeys.Count > 0)
            {
                throw Validation(
                    "Dữ liệu dòng có cột không thuộc schema thu thập.",
                    new { reportId, row.RowKey, keys = unknownKeys });
            }

            var blockedCalculatedKeys = row.Values.Keys
                .Where(key => calculatedKeys.Contains(key))
                .Take(20)
                .ToList();
            if (blockedCalculatedKeys.Count > 0)
            {
                throw Validation(
                    "Cột/hàng tính toán không được lưu như dữ liệu thu thập.",
                    new { reportId, row.RowKey, keys = blockedCalculatedKeys });
            }

            foreach (var column in spec.Columns.Where(x => x.Required))
            {
                if (!row.Values.TryGetValue(column.Key, out var value) || IsBlank(value))
                {
                    throw Validation(
                        "Dữ liệu dòng thiếu cột bắt buộc.",
                        new { reportId, row.RowKey, column = column.Key, columnLabel = column.Label });
                }
            }

            var computed = BuildCalculatedValues(spec, row.Values);
            var ruleContext = new Dictionary<string, object?>(row.Values, StringComparer.Ordinal);
            foreach (var item in computed)
                ruleContext[item.Key] = item.Value;

            foreach (var rule in spec.Rules)
            {
                if (rule.When is not null && !AsBool(Evaluate(rule.When, ruleContext)))
                    continue;

                if (!AsBool(Evaluate(rule.Condition, ruleContext)))
                {
                    throw Validation(
                        rule.Message,
                        new { reportId, row.RowKey, rule = rule.Key });
                }
            }
        }
    }

    public static List<RecordRow> ExtractRows(
        string? tableValuesJson,
        TableSpec spec,
        string? dynamicExcelTemplateId)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new List<RecordRow>();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(tableValuesJson);
        }
        catch (JsonException ex)
        {
            throw Validation("Dữ liệu bảng phát sinh không phải JSON hợp lệ.", new { ex.Message });
        }

        if (node is not JsonObject root)
            throw Validation("Dữ liệu bảng phát sinh phải là JSON object.");

        var blocks = ResolveRecordBlocks(root, dynamicExcelTemplateId);
        var collectedKeys = spec.Columns.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var calculatedKeys = spec.CalculatedOutputs.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var directRecordMetadataKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "rowKey",
            "sourceRowIndex",
            "sourceRowKey"
        };
        var rows = new List<RecordRow>();
        foreach (var block in blocks)
        {
            if (ReadNode(block, "records") is not JsonArray records)
                continue;

            foreach (var item in records)
            {
                if (item is not JsonObject record)
                    continue;

                var explicitValuesObject = ReadNode(record, "values") as JsonObject;
                var valuesObject = explicitValuesObject ?? record;
                var rawValueKeys = valuesObject
                    .Select(pair => pair.Key)
                    .Where(key => explicitValuesObject is not null || !directRecordMetadataKeys.Contains(key))
                    .ToList();
                var blockedCalculatedKeys = rawValueKeys
                    .Where(key => calculatedKeys.Contains(key))
                    .Take(20)
                    .ToList();
                if (blockedCalculatedKeys.Count > 0)
                {
                    throw Validation(
                        "Cột/hàng tính toán không được lưu như dữ liệu thu thập.",
                        new { rowKey = ReadString(record, "rowKey") ?? ReadString(record, "id"), keys = blockedCalculatedKeys });
                }

                var unknownKeys = rawValueKeys
                    .Where(key => !collectedKeys.Contains(key))
                    .Take(20)
                    .ToList();
                if (unknownKeys.Count > 0)
                {
                    throw Validation(
                        "Dữ liệu dòng có cột không thuộc schema thu thập.",
                        new { rowKey = ReadString(record, "rowKey") ?? ReadString(record, "id"), keys = unknownKeys });
                }

                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var column in spec.Columns)
                {
                    if (valuesObject.TryGetPropertyValue(column.Key, out var raw))
                        values[column.Key] = NormalizeRuntimeValue(raw, column.DataType, column.Key);
                }

                if (values.Values.All(IsBlank))
                    continue;

                var rowIndex = rows.Count;
                var rowKey = ReadString(record, "rowKey")
                             ?? ReadString(record, "id")
                             ?? $"row_{rowIndex + 1}";

                rows.Add(new RecordRow(rowKey, rowIndex, values));
            }
        }

        return rows;
    }

    public static Dictionary<string, object?> BuildCalculatedValues(
        TableSpec spec,
        IReadOnlyDictionary<string, object?> values)
    {
        var context = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var output in spec.CalculatedOutputs)
        {
            if (output.Expression is null)
                continue;

            var value = NormalizeOutputValue(Evaluate(output.Expression, context), output.DataType);
            result[output.Key] = value;
            context[output.Key] = value;
        }

        return result;
    }

    public static object? ToJsonFriendly(object? value)
    {
        if (value is DateTime date)
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return value;
    }

    private static List<JsonObject> ResolveRecordBlocks(JsonObject root, string? dynamicExcelTemplateId)
    {
        if (ReadNode(root, "blocks") is not JsonArray blocks)
            return ReadNode(root, "records") is JsonArray ? new List<JsonObject> { root } : new List<JsonObject>();

        var candidates = blocks.OfType<JsonObject>().ToList();
        if (candidates.Count == 0)
            return new List<JsonObject>();

        var templateId = dynamicExcelTemplateId?.Trim();
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var matched = candidates
                .Where(block => string.Equals(
                    ReadString(block, "dynamicExcelTemplateId") ?? ReadString(block, "excelBlockDynamicExcelTemplateId"),
                    templateId,
                    StringComparison.Ordinal))
                .ToList();
            if (matched.Count > 0)
                return matched;
        }

        var recordBlocks = candidates
            .Where(block => string.Equals(ReadString(block, "tableKind"), "RECORD_TABLE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (recordBlocks.Count > 0)
            return recordBlocks;

        return candidates.Count == 1 ? candidates : new List<JsonObject>();
    }

    private static object? NormalizeRuntimeValue(JsonNode? node, string dataType, string key)
    {
        if (node is null)
            return null;

        if (node is JsonValue value && value.TryGetValue<object?>(out var raw) && raw is null)
            return null;

        try
        {
            return dataType switch
            {
                "number" => ReadDecimal(node),
                "date" => ReadDate(node),
                "boolean" => ReadBoolean(node),
                _ => ReadText(node)
            };
        }
        catch
        {
            throw Validation(
                "Giá trị dòng không đúng kiểu dữ liệu.",
                new { key, dataType });
        }
    }

    private static object? NormalizeOutputValue(object? value, string dataType)
        => dataType switch
        {
            "number" => AsDecimal(value),
            "date" => AsDate(value),
            "boolean" => AsBool(value),
            _ => value?.ToString()
        };

    private static object? Evaluate(JsonNode? node, IReadOnlyDictionary<string, object?> values)
    {
        if (node is null)
            return null;

        if (node is not JsonObject obj)
            return LiteralValue(node);

        if (ReadString(obj, "col") is { } column)
            return values.TryGetValue(column, out var value) ? value : null;

        if (ReadNode(obj, "value") is { } valueNode)
            return LiteralValue(valueNode);

        if (ReadNode(obj, "const") is { } constNode)
            return LiteralValue(constNode);

        if (ReadString(obj, "input") is not null)
            return LiteralValue(ReadNode(obj, "value") ?? ReadNode(obj, "default") ?? new JsonObject());

        var op = (ReadString(obj, "op") ?? string.Empty).Trim().ToLowerInvariant();
        var args = ReadArgs(obj);
        return op switch
        {
            "today" or "currentdate" => DateTime.UtcNow.Date,
            "add" or "sum" => args.Select(x => AsDecimal(Evaluate(x, values)) ?? 0m).Sum(),
            "subtract" or "sub" => (AsDecimal(Evaluate(args.ElementAtOrDefault(0), values)) ?? 0m) - (AsDecimal(Evaluate(args.ElementAtOrDefault(1), values)) ?? 0m),
            "multiply" or "mul" => args.Select(x => AsDecimal(Evaluate(x, values)) ?? 0m).Aggregate(1m, (a, b) => a * b),
            "divide" or "div" => Divide(args, values),
            "datediffdays" => DateDiffDays(args, values),
            "gt" => Compare(args, values) > 0,
            "gte" => Compare(args, values) >= 0,
            "lt" => Compare(args, values) < 0,
            "lte" => Compare(args, values) <= 0,
            "eq" => Compare(args, values) == 0,
            "neq" => Compare(args, values) != 0,
            "and" => args.All(x => AsBool(Evaluate(x, values))),
            "or" => args.Any(x => AsBool(Evaluate(x, values))),
            "not" => !AsBool(Evaluate(args.FirstOrDefault(), values)),
            "isblank" => IsBlank(Evaluate(args.FirstOrDefault(), values)),
            "isnotblank" => !IsBlank(Evaluate(args.FirstOrDefault(), values)),
            "if" => AsBool(Evaluate(args.ElementAtOrDefault(0), values))
                ? Evaluate(args.ElementAtOrDefault(1), values)
                : Evaluate(args.ElementAtOrDefault(2), values),
            _ => null
        };
    }

    private static decimal? Divide(IReadOnlyList<JsonNode> args, IReadOnlyDictionary<string, object?> values)
    {
        var left = AsDecimal(Evaluate(args.ElementAtOrDefault(0), values)) ?? 0m;
        var right = AsDecimal(Evaluate(args.ElementAtOrDefault(1), values)) ?? 0m;
        return right == 0m ? null : left / right;
    }

    private static decimal? DateDiffDays(IReadOnlyList<JsonNode> args, IReadOnlyDictionary<string, object?> values)
    {
        var left = AsDate(Evaluate(args.ElementAtOrDefault(0), values));
        var right = AsDate(Evaluate(args.ElementAtOrDefault(1), values));
        if (!left.HasValue || !right.HasValue)
            return null;

        return (decimal)(right.Value.Date - left.Value.Date).TotalDays;
    }

    private static int Compare(IReadOnlyList<JsonNode> args, IReadOnlyDictionary<string, object?> values)
    {
        var left = Evaluate(args.ElementAtOrDefault(0), values);
        var right = Evaluate(args.ElementAtOrDefault(1), values);
        if (left is null && right is null) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var leftNumber = AsDecimal(left);
        var rightNumber = AsDecimal(right);
        if (leftNumber.HasValue && rightNumber.HasValue)
            return leftNumber.Value.CompareTo(rightNumber.Value);

        var leftDate = AsDate(left);
        var rightDate = AsDate(right);
        if (leftDate.HasValue && rightDate.HasValue)
            return leftDate.Value.Date.CompareTo(rightDate.Value.Date);

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static List<JsonNode> ReadArgs(JsonObject obj)
    {
        if (ReadNode(obj, "args") is JsonArray args)
            return args.Where(x => x is not null).Select(x => x!).ToList();

        var result = new List<JsonNode>();
        if (ReadNode(obj, "left") is { } left) result.Add(left);
        if (ReadNode(obj, "right") is { } right) result.Add(right);
        return result;
    }

    private static object? LiteralValue(JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var number)) return number;
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<string>(out var text)) return text;
        }

        return null;
    }

    private static decimal? ReadDecimal(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var number)) return number;
            if (value.TryGetValue<double>(out var doubleNumber) && double.IsFinite(doubleNumber)) return (decimal)doubleNumber;
            if (value.TryGetValue<string>(out var text) &&
                decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTime? ReadDate(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<DateTime>(out var date)) return date.Date;
            if (value.TryGetValue<string>(out var text) &&
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.Date;
            }
        }

        return null;
    }

    private static bool? ReadBoolean(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)) return parsed;
        }

        return null;
    }

    private static string? ReadText(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            if (value.TryGetValue<decimal>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<bool>(out var boolean)) return boolean ? "true" : "false";
        }

        return null;
    }

    private static decimal? AsDecimal(object? value)
        => value switch
        {
            decimal number => number,
            int number => number,
            long number => number,
            double number when double.IsFinite(number) => (decimal)number,
            string text when decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

    private static DateTime? AsDate(object? value)
        => value switch
        {
            DateTime date => date.Date,
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) => parsed.Date,
            _ => null
        };

    private static bool AsBool(object? value)
        => value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            decimal number => number != 0m,
            int number => number != 0,
            _ => false
        };

    private static bool IsBlank(object? value)
        => value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    private static List<JsonObject> ReadObjectArray(JsonObject root, string field)
        => ReadNode(root, field) is JsonArray array
            ? array.OfType<JsonObject>().ToList()
            : new List<JsonObject>();

    private static JsonNode? ReadExpression(JsonObject obj)
        => ReadNode(obj, "expression") ?? ReadNode(obj, "expr") ?? ReadNode(obj, "condition");

    private static JsonNode? ReadNode(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var value) ? value : null;

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

    private static string RequireKey(JsonObject obj, string field)
    {
        var key = ReadString(obj, "key");
        if (string.IsNullOrWhiteSpace(key))
            throw Validation("Mã cột/hàng không được trống.", new { field });

        return key.Trim();
    }

    private static string NormalizeDataType(string? raw)
    {
        var normalized = (raw ?? "text").Trim().ToLowerInvariant();
        return normalized switch
        {
            "string" => "text",
            "decimal" => "number",
            "datetime" => "date",
            "bool" => "boolean",
            "text" or "number" or "date" or "boolean" => normalized,
            _ => "text"
        };
    }

    private static string? ReadString(JsonObject obj, string name)
        => ReadNode(obj, name) is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool? ReadBool(JsonObject obj, string name)
        => ReadNode(obj, name) is JsonValue value && value.TryGetValue<bool>(out var boolean)
            ? boolean
            : null;

    private static AppException Validation(string message, object? details = null)
        => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, details, message);
}
