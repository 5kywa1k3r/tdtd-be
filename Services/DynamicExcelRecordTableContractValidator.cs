using System.Text.Json;
using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;

namespace tdtd_be.Services;

public static class DynamicExcelRecordTableContractValidator
{
    private const int MaxInputColumns = 100;
    private const int MaxCalculatedOutputs = 10;
    private const int MaxValidationRules = 100;
    private const int MaxExpressionDepth = 10;
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    public static void Validate(string? recordTableSpecJson)
    {
        if (string.IsNullOrWhiteSpace(recordTableSpecJson))
            Fail("Thiếu cấu hình bảng dữ liệu phát sinh.", new { field = "recordTableSpecJson" });

        using var document = Parse(recordTableSpecJson!);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            Fail("Cấu hình bảng dữ liệu phát sinh phải là JSON object.", new { field = "recordTableSpecJson" });

        var orientation = (ReadString(root, "orientation") ?? "ROWS").Trim().ToUpperInvariant();
        if (orientation is not ("ROWS" or "COLUMNS"))
            Fail("Hướng bảng dữ liệu phát sinh phải là ROWS hoặc COLUMNS.", new { field = "orientation", orientation });

        var columns = ReadObjectArray(root, "columns", required: true);
        if (columns.Count == 0 || columns.Count > MaxInputColumns)
            Fail("Số cột dữ liệu thu thập phải nằm trong 1..100.", new { field = "columns", count = columns.Count, max = MaxInputColumns });

        var collectedColumns = new Dictionary<string, ContractColumn>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var key = RequireKey(column, "columns[].key");
            if (collectedColumns.ContainsKey(key))
                Fail("Mã cột dữ liệu thu thập bị trùng.", new { field = "columns[].key", key });

            collectedColumns[key] = new ContractColumn(
                key,
                NormalizeDataType(ReadString(column, "dataType") ?? ReadString(column, "type"), $"columns[{key}].dataType"));
        }

        var allColumns = new Dictionary<string, ContractColumn>(collectedColumns, StringComparer.Ordinal);
        var calculated = ReadObjectArray(root, "calculatedColumns", required: false)
            .Concat(ReadObjectArray(root, "calculatedRows", required: false))
            .Concat(ReadObjectArray(root, "aggregateColumns", required: false))
            .Concat(ReadObjectArray(root, "aggregateRows", required: false))
            .ToList();

        if (calculated.Count > MaxCalculatedOutputs)
            Fail("Tổng số cột hoặc hàng tính toán của bảng dữ liệu phát sinh không được vượt quá 10.", new
            {
                fields = new[] { "calculatedColumns", "calculatedRows", "aggregateColumns", "aggregateRows" },
                count = calculated.Count,
                max = MaxCalculatedOutputs
            });

        foreach (var item in calculated)
        {
            var key = RequireKey(item, "calculated.key");
            if (allColumns.ContainsKey(key))
                Fail("Mã cột/hàng tính toán bị trùng với cột dữ liệu hoặc output khác.", new { key });

            if (ReadBool(item, "includeInUpstream") == true ||
                ReadBool(item, "isCollected") == true ||
                ReadBool(item, "collect") == true)
            {
                Fail("Cột/hàng tính toán không được đánh dấu là dữ liệu thu thập cho cấp trên.", new { key });
            }

            var declaredType = NormalizeDataType(ReadString(item, "dataType") ?? ReadString(item, "type"), $"calculated[{key}].dataType");
            var expr = ReadExpression(item)
                ?? throw Validation("Cột/hàng tính toán phải có expression.", new { key, field = "expression" });
            var inferred = InferExpressionType(expr, collectedColumns, depth: 0);
            EnsureAssignable(declaredType, inferred, $"calculated[{key}].expression", key);
            allColumns[key] = new ContractColumn(key, declaredType);
        }

        var rules = ReadObjectArray(root, "validationRules", required: false)
            .Concat(ReadObjectArray(root, "rowRules", required: false))
            .Concat(ReadObjectArray(root, "columnRules", required: false))
            .ToList();

        if (rules.Count > MaxValidationRules)
            Fail("Số điều kiện kiểm tra dữ liệu không được vượt quá 100.", new { count = rules.Count, max = MaxValidationRules });

        foreach (var rule in rules)
        {
            var ruleKey = ReadString(rule, "key") ?? ReadString(rule, "id") ?? "(không đặt mã)";
            var condition = ReadExpression(rule)
                ?? throw Validation("Điều kiện kiểm tra dữ liệu phải có expression/condition.", new { key = ruleKey });
            var conditionType = InferExpressionType(condition, allColumns, depth: 0);
            if (conditionType != ContractValueType.Boolean)
                Fail("Điều kiện kiểm tra dữ liệu phải trả về kiểu đúng/sai.", new { key = ruleKey, actualType = conditionType.ToString() });

            if (rule.TryGetProperty("when", out var when) && when.ValueKind != JsonValueKind.Null)
            {
                var whenType = InferExpressionType(when, allColumns, depth: 0);
                if (whenType != ContractValueType.Boolean)
                    Fail("Điều kiện when phải trả về kiểu đúng/sai.", new { key = ruleKey, actualType = whenType.ToString() });
            }
        }
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Validation("Cấu hình bảng dữ liệu phát sinh không phải JSON hợp lệ.", new { ex.Message });
        }
    }

    private static List<JsonElement> ReadObjectArray(JsonElement root, string field, bool required)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
                Fail("Thiếu danh sách cột dữ liệu thu thập.", new { field });
            return new List<JsonElement>();
        }

        if (value.ValueKind != JsonValueKind.Array)
            Fail("Trường cấu hình phải là danh sách.", new { field });

        var list = new List<JsonElement>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                Fail("Mỗi phần tử cấu hình phải là object.", new { field });
            list.Add(item);
        }
        return list;
    }

    private static string RequireKey(JsonElement element, string field)
    {
        var key = ReadString(element, "key") ?? string.Empty;
        if (!KeyPattern.IsMatch(key))
            Fail("Mã cột/hàng phải bắt đầu bằng chữ cái và chỉ gồm chữ, số, dấu gạch dưới; tối đa 64 ký tự.", new { field, key });
        return key;
    }

    private static JsonElement? ReadExpression(JsonElement element)
    {
        if (element.TryGetProperty("expression", out var expression)) return expression;
        if (element.TryGetProperty("expr", out var expr)) return expr;
        if (element.TryGetProperty("condition", out var condition)) return condition;
        return null;
    }

    private static ContractValueType InferExpressionType(
        JsonElement expression,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (depth > MaxExpressionDepth)
            Fail("Biểu thức tính toán/kiểm tra quá sâu.", new { maxDepth = MaxExpressionDepth });

        if (expression.ValueKind != JsonValueKind.Object)
            return InferLiteralType(expression);

        if (expression.TryGetProperty("col", out var colElement))
        {
            var col = colElement.GetString()?.Trim() ?? string.Empty;
            return columns.TryGetValue(col, out var definition)
                ? definition.Type
                : throw Validation("Biểu thức tham chiếu tới cột không tồn tại.", new { col });
        }

        if (expression.TryGetProperty("input", out _))
            return NormalizeDataType(ReadString(expression, "dataType") ?? ReadString(expression, "type"), "input.dataType");

        if (expression.TryGetProperty("value", out var valueElement))
            return ReadString(expression, "dataType") is { } valueType
                ? NormalizeDataType(valueType, "value.dataType")
                : InferLiteralType(valueElement);

        if (expression.TryGetProperty("const", out var constElement))
            return ReadString(expression, "dataType") is { } constType
                ? NormalizeDataType(constType, "const.dataType")
                : InferLiteralType(constElement);

        var op = (ReadString(expression, "op") ?? string.Empty).Trim().ToLowerInvariant();
        var args = ReadArgs(expression);

        return op switch
        {
            "today" or "currentdate" => ContractValueType.Date,
            "add" or "sum" or "subtract" or "sub" or "multiply" or "mul" or "divide" or "div" => InferNumberOp(op, args, columns, depth),
            "datediffdays" => InferDateDiff(args, columns, depth),
            "gt" or "gte" or "lt" or "lte" or "eq" or "neq" => InferCompare(op, args, columns, depth),
            "and" or "or" => InferBooleanMany(op, args, columns, depth),
            "not" => InferNot(args, columns, depth),
            "isblank" or "isnotblank" => InferBlankCheck(args, columns, depth),
            "if" => InferIf(args, columns, depth),
            _ => throw Validation("Toán tử biểu thức không được hỗ trợ.", new { op })
        };
    }

    private static List<JsonElement> ReadArgs(JsonElement expression)
    {
        if (expression.TryGetProperty("args", out var argsElement))
        {
            if (argsElement.ValueKind != JsonValueKind.Array)
                Fail("args phải là danh sách.", new { field = "args" });
            return argsElement.EnumerateArray().ToList();
        }

        var args = new List<JsonElement>();
        if (expression.TryGetProperty("left", out var left)) args.Add(left);
        if (expression.TryGetProperty("right", out var right)) args.Add(right);
        return args;
    }

    private static ContractValueType InferNumberOp(
        string op,
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        var min = op is "add" or "sum" or "multiply" or "mul" ? 2 : 2;
        if (args.Count < min)
            Fail("Phép tính số cần ít nhất hai tham số.", new { op, argCount = args.Count });

        foreach (var arg in args)
            RequireType(InferExpressionType(arg, columns, depth + 1), ContractValueType.Number, op);
        return ContractValueType.Number;
    }

    private static ContractValueType InferDateDiff(
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (args.Count != 2)
            Fail("dateDiffDays cần đúng hai tham số ngày.", new { argCount = args.Count });
        RequireType(InferExpressionType(args[0], columns, depth + 1), ContractValueType.Date, "dateDiffDays");
        RequireType(InferExpressionType(args[1], columns, depth + 1), ContractValueType.Date, "dateDiffDays");
        return ContractValueType.Number;
    }

    private static ContractValueType InferCompare(
        string op,
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (args.Count != 2)
            Fail("Phép so sánh cần đúng hai tham số.", new { op, argCount = args.Count });

        var left = InferExpressionType(args[0], columns, depth + 1);
        var right = InferExpressionType(args[1], columns, depth + 1);
        if (left != right)
            Fail("Hai vế so sánh phải cùng kiểu dữ liệu.", new { op, leftType = left.ToString(), rightType = right.ToString() });

        if (op is "gt" or "gte" or "lt" or "lte" && left is not (ContractValueType.Number or ContractValueType.Date))
            Fail("Phép so sánh lớn/nhỏ chỉ hỗ trợ kiểu số hoặc ngày.", new { op, dataType = left.ToString() });

        return ContractValueType.Boolean;
    }

    private static ContractValueType InferBooleanMany(
        string op,
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (args.Count < 2)
            Fail("Phép logic cần ít nhất hai điều kiện.", new { op, argCount = args.Count });
        foreach (var arg in args)
            RequireType(InferExpressionType(arg, columns, depth + 1), ContractValueType.Boolean, op);
        return ContractValueType.Boolean;
    }

    private static ContractValueType InferNot(
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (args.Count != 1)
            Fail("Phép not cần đúng một điều kiện.", new { argCount = args.Count });
        RequireType(InferExpressionType(args[0], columns, depth + 1), ContractValueType.Boolean, "not");
        return ContractValueType.Boolean;
    }

    private static ContractValueType InferBlankCheck(
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (args.Count != 1)
            Fail("Phép kiểm tra rỗng cần đúng một tham số.", new { argCount = args.Count });
        _ = InferExpressionType(args[0], columns, depth + 1);
        return ContractValueType.Boolean;
    }

    private static ContractValueType InferIf(
        IReadOnlyList<JsonElement> args,
        IReadOnlyDictionary<string, ContractColumn> columns,
        int depth)
    {
        if (args.Count != 3)
            Fail("Phép if cần đúng ba tham số: điều kiện, giá trị đúng, giá trị sai.", new { argCount = args.Count });

        RequireType(InferExpressionType(args[0], columns, depth + 1), ContractValueType.Boolean, "if");
        var whenTrue = InferExpressionType(args[1], columns, depth + 1);
        var whenFalse = InferExpressionType(args[2], columns, depth + 1);
        if (whenTrue != whenFalse)
            Fail("Hai nhánh kết quả của if phải cùng kiểu dữ liệu.", new { trueType = whenTrue.ToString(), falseType = whenFalse.ToString() });
        return whenTrue;
    }

    private static void EnsureAssignable(ContractValueType declared, ContractValueType inferred, string field, string key)
    {
        if (declared != inferred)
            Fail("Kiểu dữ liệu khai báo không khớp với biểu thức.", new
            {
                field,
                key,
                declaredType = declared.ToString(),
                inferredType = inferred.ToString()
            });
    }

    private static void RequireType(ContractValueType actual, ContractValueType expected, string op)
    {
        if (actual != expected)
            Fail("Tham số biểu thức không đúng kiểu dữ liệu.", new { op, expectedType = expected.ToString(), actualType = actual.ToString() });
    }

    private static ContractValueType InferLiteralType(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number => ContractValueType.Number,
            JsonValueKind.True or JsonValueKind.False => ContractValueType.Boolean,
            JsonValueKind.String => ContractValueType.Text,
            JsonValueKind.Null => ContractValueType.Text,
            _ => throw Validation("Giá trị hằng trong biểu thức không hợp lệ.", new { valueKind = element.ValueKind.ToString() })
        };

    private static ContractValueType NormalizeDataType(string? raw, string field)
    {
        var normalized = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "TEXT" or "STRING" => ContractValueType.Text,
            "NUMBER" or "DECIMAL" => ContractValueType.Number,
            "DATE" or "DATETIME" => ContractValueType.Date,
            "BOOLEAN" or "BOOL" => ContractValueType.Boolean,
            _ => throw Validation("Kiểu dữ liệu không được hỗ trợ.", new
            {
                field,
                dataType = raw,
                allowed = new[] { "text", "number", "date", "boolean" }
            })
        };
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool? ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static void Fail(string message, object? details = null)
        => throw Validation(message, details);

    private static AppException Validation(string message, object? details = null)
        => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, details, message);

    private sealed record ContractColumn(string Key, ContractValueType Type);

    private enum ContractValueType
    {
        Text,
        Number,
        Date,
        Boolean
    }
}
