using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.Services.DynamicFlows;

internal sealed class DynamicFlowMappingEvaluationException : Exception
{
    public DynamicFlowMappingEvaluationException(string reason, string? detail = null)
        : base(detail is null ? reason : $"{reason}: {detail}")
    {
        Reason = reason;
    }

    public string Reason { get; }
}

internal static class DynamicFlowMappingExpressionEvaluator
{
    public const string EvaluatorVersion = "P7-EVAL-1";

    private const int MaxDepth = 12;
    private const int MaxNodes = 128;

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "copy", "firstNonBlank", "coalesce",
        "add", "subtract", "multiply", "divide", "round",
        "sum", "count", "min", "max", "average", "distinctCount",
        "concat", "textJoin", "trim", "upper", "lower",
        "dateDiffDays", "dateAddDays",
        "if", "and", "or", "not", "eq", "gt", "gte", "lt", "lte",
        "toJson"
    };

    public static string? Validate(
        JsonNode? expression,
        IReadOnlyDictionary<string, string> inputDataTypes)
    {
        if (expression is null)
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_REQUIRED");

        var nodeCount = 0;
        return ValidateNode(expression, inputDataTypes, 0, ref nodeCount);
    }

    public static void EnsureVersion(string? evaluatorVersion)
    {
        if (string.Equals(evaluatorVersion, EvaluatorVersion, StringComparison.Ordinal))
            return;

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_EVALUATOR_VERSION_MISMATCH");
    }

    public static JsonNode? Evaluate(
        JsonNode? expression,
        IReadOnlyDictionary<string, JsonNode?> inputs)
    {
        if (expression is null)
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_REQUIRED");

        var nodeCount = 0;
        return EvaluateNode(expression, inputs, 0, ref nodeCount);
    }

    public static JsonNode? Evaluate(
        JsonNode? expression,
        IReadOnlyDictionary<string, JsonNode?> inputs,
        IReadOnlyDictionary<string, string> inputDataTypes)
    {
        var resultDataType = Validate(expression, inputDataTypes);
        ValidateRuntimeInputs(expression!, inputs, inputDataTypes);

        var nodeCount = 0;
        var result = EvaluateNode(expression!, inputs, 0, ref nodeCount);
        if (!string.IsNullOrWhiteSpace(resultDataType))
        {
            DynamicFlowMappingFieldValueContract.ValidateJsonNative(
                result,
                resultDataType,
                allowedChoiceCodes: null,
                allowNull: true);
        }

        return result;
    }

    private static string? ValidateNode(
        JsonNode node,
        IReadOnlyDictionary<string, string> inputDataTypes,
        int depth,
        ref int nodeCount)
    {
        GuardComplexity(depth, ref nodeCount);

        if (node is JsonValue)
            return InferLiteralType(node);

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                    ValidateNode(item, inputDataTypes, depth + 1, ref nodeCount);
            }

            return "JSON";
        }

        if (node is not JsonObject obj)
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_NODE_INVALID");

        ValidateObjectShape(obj);

        var inputKey = ReadString(obj, "input");
        if (inputKey is not null)
        {
            if (!inputDataTypes.TryGetValue(inputKey, out var inputDataType))
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_INPUT_UNKNOWN",
                    inputKey);
            }

            return inputDataType;
        }

        if (obj.ContainsKey("value") && !obj.ContainsKey("op"))
        {
            var declaredDataType = ReadString(obj, "dataType");
            if (!string.IsNullOrWhiteSpace(declaredDataType))
            {
                DynamicFlowMappingFieldValueContract.ValidateJsonNative(
                    obj["value"],
                    declaredDataType,
                    allowedChoiceCodes: null,
                    allowNull: true);
                return DynamicFlowMappingFieldValueContract.NormalizeDataType(declaredDataType);
            }

            return InferLiteralType(obj["value"]);
        }

        var operation = ReadString(obj, "op")
            ?? throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_REQUIRED");
        if (!AllowedOperations.Contains(operation))
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_UNSUPPORTED",
                operation);
        }

        var arguments = ReadArgumentNodes(obj).ToList();
        ValidateArgumentCount(operation, arguments.Count);
        var argumentTypes = new List<string?>();
        foreach (var argument in arguments)
            argumentTypes.Add(ValidateNode(argument, inputDataTypes, depth + 1, ref nodeCount));
        ValidateOperationDataTypes(operation, argumentTypes);

        return InferOperationType(operation) ?? InferPassthroughType(operation, argumentTypes);
    }

    private static JsonNode? EvaluateNode(
        JsonNode node,
        IReadOnlyDictionary<string, JsonNode?> inputs,
        int depth,
        ref int nodeCount)
    {
        GuardComplexity(depth, ref nodeCount);

        if (node is JsonValue)
            return node.DeepClone();

        if (node is JsonArray array)
        {
            var evaluatedItems = new JsonArray();
            foreach (var item in array)
            {
                evaluatedItems.Add(item is null
                    ? null
                    : EvaluateNode(item, inputs, depth + 1, ref nodeCount));
            }

            return evaluatedItems;
        }

        if (node is not JsonObject obj)
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_NODE_INVALID");

        ValidateObjectShape(obj);

        var inputKey = ReadString(obj, "input");
        if (inputKey is not null)
        {
            if (!inputs.TryGetValue(inputKey, out var inputValue))
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_INPUT_UNKNOWN",
                    inputKey);
            }

            return inputValue?.DeepClone();
        }

        if (obj.ContainsKey("value") && !obj.ContainsKey("op"))
            return obj["value"]?.DeepClone();

        var operation = ReadString(obj, "op")
            ?? throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_REQUIRED");
        if (!AllowedOperations.Contains(operation))
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_UNSUPPORTED",
                operation);
        }

        var argumentNodes = ReadArgumentNodes(obj).ToList();
        ValidateArgumentCount(operation, argumentNodes.Count);
        var args = new List<JsonNode?>();
        foreach (var argument in argumentNodes)
            args.Add(EvaluateNode(argument, inputs, depth + 1, ref nodeCount));
        return EvaluateOperation(operation, args, obj);
    }

    private static JsonNode? EvaluateOperation(
        string operation,
        IReadOnlyList<JsonNode?> arguments,
        JsonObject expression)
    {
        var op = operation.Trim().ToLowerInvariant();
        var flat = Flatten(arguments).ToList();

        switch (op)
        {
            case "copy":
                return arguments[0]?.DeepClone();
            case "firstnonblank":
            case "coalesce":
                return flat.FirstOrDefault(value => !IsBlank(value))?.DeepClone();
            case "sum":
            case "add":
                return JsonValue.Create(ReadNumbers(flat, op).Sum());
            case "subtract":
            {
                var numbers = ReadNumbers(flat, op);
                RequireArgumentCount(numbers.Count, 2, op);
                return JsonValue.Create(numbers.Skip(1).Aggregate(numbers[0], (current, value) => current - value));
            }
            case "multiply":
            {
                var numbers = ReadNumbers(flat, op);
                if (numbers.Count == 0)
                    return null;
                return JsonValue.Create(numbers.Aggregate(1m, (current, value) => current * value));
            }
            case "divide":
            {
                RequireExactArgumentCount(flat.Count, 2, op);
                var numbers = ReadNumbers(flat, op);
                if (numbers[1] == 0m)
                    throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_DIVIDE_BY_ZERO");
                return JsonValue.Create(numbers[0] / numbers[1]);
            }
            case "round":
            {
                RequireArgumentRange(flat.Count, 1, 2, op);
                var numbers = ReadNumbers(flat, op);
                var digits = numbers.Count > 1 ? Convert.ToInt32(numbers[1], CultureInfo.InvariantCulture) : 0;
                if (digits is < 0 or > 10)
                    throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_ROUND_DIGITS_INVALID");
                return JsonValue.Create(Math.Round(numbers[0], digits, MidpointRounding.AwayFromZero));
            }
            case "count":
                return JsonValue.Create(flat.Count(value => !IsBlank(value)));
            case "min":
            case "max":
            case "average":
            {
                var numbers = ReadNumbers(flat, op);
                if (numbers.Count == 0)
                    return null;
                return JsonValue.Create(op switch
                {
                    "min" => numbers.Min(),
                    "max" => numbers.Max(),
                    _ => numbers.Average()
                });
            }
            case "distinctcount":
                return JsonValue.Create(flat.Where(value => !IsBlank(value)).Select(StableJson).Distinct(StringComparer.Ordinal).Count());
            case "concat":
            case "textjoin":
            {
                var separator = ReadLiteralString(expression, "separator") ?? (op == "textjoin" ? ", " : string.Empty);
                return JsonValue.Create(string.Join(
                    separator,
                    flat
                        .Where(value => !IsBlank(value))
                        .Select(value => ReadText(value, op))));
            }
            case "trim":
            case "upper":
            case "lower":
            {
                var first = flat.FirstOrDefault(value => !IsBlank(value));
                var text = first is null ? string.Empty : ReadText(first, op);
                return JsonValue.Create(op switch
                {
                    "upper" => text.ToUpperInvariant(),
                    "lower" => text.ToLowerInvariant(),
                    _ => text.Trim()
                });
            }
            case "datediffdays":
            {
                RequireExactArgumentCount(flat.Count, 2, op);
                var from = ReadDate(flat[0], op);
                var to = ReadDate(flat[1], op);
                return JsonValue.Create((to.Date - from.Date).Days);
            }
            case "dateadddays":
            {
                RequireExactArgumentCount(flat.Count, 2, op);
                var date = ReadDate(flat[0], op);
                var days = ReadNumber(flat[1], op);
                return JsonValue.Create(date
                    .AddDays(Convert.ToDouble(days, CultureInfo.InvariantCulture))
                    .ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            }
            case "if":
                RequireExactArgumentCount(arguments.Count, 3, op);
                return ReadBoolean(arguments[0], op) ? arguments[1]?.DeepClone() : arguments[2]?.DeepClone();
            case "and":
                return JsonValue.Create(flat.All(value => ReadBoolean(value, op)));
            case "or":
                return JsonValue.Create(flat.Any(value => ReadBoolean(value, op)));
            case "not":
                RequireExactArgumentCount(flat.Count, 1, op);
                return JsonValue.Create(!ReadBoolean(flat[0], op));
            case "eq":
                RequireExactArgumentCount(flat.Count, 2, op);
                return JsonValue.Create(Compare(flat[0], flat[1]) == 0);
            case "gt":
            case "gte":
            case "lt":
            case "lte":
            {
                RequireExactArgumentCount(flat.Count, 2, op);
                var comparison = Compare(flat[0], flat[1]);
                return JsonValue.Create(op switch
                {
                    "gt" => comparison > 0,
                    "gte" => comparison >= 0,
                    "lt" => comparison < 0,
                    _ => comparison <= 0
                });
            }
            case "tojson":
                return arguments.Count == 1
                    ? arguments[0]?.DeepClone()
                    : new JsonArray(arguments.Select(value => value?.DeepClone()).ToArray());
            default:
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_UNSUPPORTED",
                    operation);
        }
    }

    private static IEnumerable<JsonNode> ReadArgumentNodes(JsonObject expression)
    {
        if (expression["args"] is JsonArray args)
        {
            foreach (var item in args)
            {
                if (item is not null)
                    yield return item;
            }

            yield break;
        }

        foreach (var name in new[] { "condition", "left", "right", "then", "else", "value" })
        {
            if (expression[name] is JsonNode item)
                yield return item;
        }
    }

    private static void ValidateRuntimeInputs(
        JsonNode node,
        IReadOnlyDictionary<string, JsonNode?> inputs,
        IReadOnlyDictionary<string, string> inputDataTypes)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                    ValidateRuntimeInputs(item, inputs, inputDataTypes);
            }

            return;
        }

        if (node is not JsonObject obj)
            return;

        var inputKey = ReadString(obj, "input");
        if (inputKey is not null)
        {
            if (!inputDataTypes.TryGetValue(inputKey, out var dataType) ||
                !inputs.TryGetValue(inputKey, out var inputValue))
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_INPUT_UNKNOWN",
                    inputKey);
            }

            DynamicFlowMappingFieldValueContract.ValidateJsonNative(
                inputValue,
                dataType,
                allowedChoiceCodes: null,
                allowNull: true);
            return;
        }

        if (obj.ContainsKey("value") && !obj.ContainsKey("op"))
            return;

        foreach (var argument in ReadArgumentNodes(obj))
            ValidateRuntimeInputs(argument, inputs, inputDataTypes);
    }

    private static void ValidateObjectShape(JsonObject expression)
    {
        if (expression.ContainsKey("input"))
        {
            EnsureOnlyProperties(expression, "input");
            if (ReadString(expression, "input") is null)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_INPUT_REQUIRED");
            }

            return;
        }

        if (expression.ContainsKey("op"))
        {
            EnsureOnlyProperties(
                expression,
                "op",
                "args",
                "condition",
                "left",
                "right",
                "then",
                "else",
                "value",
                "separator");

            var operation = ReadString(expression, "op")
                ?? throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_REQUIRED");
            var hasNamedArguments = new[] { "condition", "left", "right", "then", "else", "value" }
                .Any(expression.ContainsKey);
            if (expression.ContainsKey("args") && expression["args"] is not JsonArray)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_ARGUMENTS_ARRAY_REQUIRED");
            }

            if (expression.ContainsKey("args") && hasNamedArguments)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_EXPRESSION_ARGUMENTS_AMBIGUOUS");
            }

            if (expression.ContainsKey("separator"))
            {
                if (!operation.Equals("concat", StringComparison.OrdinalIgnoreCase) &&
                    !operation.Equals("textJoin", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_EXPRESSION_SEPARATOR_UNSUPPORTED",
                        operation);
                }

                if (expression["separator"] is not JsonValue separator ||
                    !separator.TryGetValue<string>(out _))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_EXPRESSION_SEPARATOR_STRING_REQUIRED");
                }
            }

            return;
        }

        if (expression.ContainsKey("value"))
        {
            EnsureOnlyProperties(expression, "value", "dataType");
            if (expression.ContainsKey("dataType") &&
                ReadString(expression, "dataType") is null)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_FIELD_DATA_TYPE_REQUIRED");
            }

            return;
        }

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_REQUIRED");
    }

    private static void EnsureOnlyProperties(
        JsonObject expression,
        params string[] allowedProperties)
    {
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var unsupported = expression
            .Select(property => property.Key)
            .FirstOrDefault(property => !allowed.Contains(property));
        if (unsupported is not null)
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_EXPRESSION_PROPERTY_UNSUPPORTED",
                unsupported);
        }
    }

    private static IEnumerable<JsonNode?> Flatten(IEnumerable<JsonNode?> values)
    {
        foreach (var value in values)
        {
            if (value is JsonArray array)
            {
                foreach (var nested in Flatten(array))
                    yield return nested;
                continue;
            }

            yield return value;
        }
    }

    private static List<decimal> ReadNumbers(IEnumerable<JsonNode?> values, string operation)
    {
        var result = new List<decimal>();
        foreach (var value in values.Where(value => !IsBlank(value)))
            result.Add(ReadNumber(value, operation));
        return result;
    }

    private static decimal ReadNumber(JsonNode? value, string operation)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<decimal>(out var decimalValue))
                return decimalValue;
            if (jsonValue.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
                return (decimal)doubleValue;
        }

        throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_NUMBER_INVALID", operation);
    }

    private static DateTime ReadDate(JsonNode? value, string operation)
    {
        if (value is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var text) ||
            string.IsNullOrEmpty(text))
        {
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_DATE_INVALID", operation);
        }

        if (DateTime.TryParseExact(
                text,
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exact))
        {
            return exact;
        }

        throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_DATE_INVALID", operation);
    }

    private static bool ReadBoolean(JsonNode? value, string operation)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolean))
                return boolean;
        }

        throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_BOOLEAN_INVALID", operation);
    }

    private static int Compare(JsonNode? left, JsonNode? right)
    {
        if (TryReadNumber(left, out var leftNumber) &&
            TryReadNumber(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is JsonValue leftValue &&
            right is JsonValue rightValue &&
            leftValue.TryGetValue<string>(out var leftText) &&
            rightValue.TryGetValue<string>(out var rightText))
        {
            return string.Compare(leftText, rightText, StringComparison.Ordinal);
        }

        if (left is JsonValue leftBooleanValue &&
            right is JsonValue rightBooleanValue &&
            leftBooleanValue.TryGetValue<bool>(out var leftBoolean) &&
            rightBooleanValue.TryGetValue<bool>(out var rightBoolean))
        {
            return leftBoolean.CompareTo(rightBoolean);
        }

        if (left is null && right is null)
            return 0;

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_COMPARISON_TYPE_MISMATCH");
    }

    private static bool TryReadNumber(JsonNode? value, out decimal result)
    {
        try
        {
            result = ReadNumber(value, "compare");
            return true;
        }
        catch (DynamicFlowMappingEvaluationException)
        {
            result = 0m;
            return false;
        }
    }

    private static void RequireArgumentCount(int actual, int minimum, string operation)
    {
        if (actual < minimum)
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_EXPRESSION_ARGUMENTS_REQUIRED",
                $"{operation}:{minimum}");
        }
    }

    private static void RequireExactArgumentCount(int actual, int expected, string operation)
        => RequireArgumentRange(actual, expected, expected, operation);

    private static void RequireArgumentRange(int actual, int minimum, int maximum, string operation)
    {
        if (actual >= minimum && actual <= maximum)
            return;

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_ARGUMENT_COUNT_INVALID",
            $"{operation}:{minimum}:{maximum}");
    }

    private static void ValidateArgumentCount(string operation, int actual)
    {
        var op = operation.Trim().ToLowerInvariant();
        var (minimum, maximum) = op switch
        {
            "if" => (3, 3),
            "copy" => (1, 1),
            "round" => (1, 2),
            "trim" or "upper" or "lower" or "not" => (1, 1),
            "divide" or "datediffdays" or "dateadddays" or "eq" or "gt" or "gte" or "lt" or "lte" => (2, 2),
            "subtract" => (2, int.MaxValue),
            _ => (1, int.MaxValue)
        };
        if (actual >= minimum && actual <= maximum)
            return;

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_ARGUMENT_COUNT_INVALID",
            $"{operation}:{minimum}:{(maximum == int.MaxValue ? "*" : maximum.ToString(CultureInfo.InvariantCulture))}");
    }

    private static void GuardComplexity(int depth, ref int nodeCount)
    {
        nodeCount++;
        if (depth > MaxDepth || nodeCount > MaxNodes)
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_EXPRESSION_TOO_COMPLEX");
    }

    private static void ValidateOperationDataTypes(
        string operation,
        IReadOnlyList<string?> argumentTypes)
    {
        var op = operation.Trim().ToLowerInvariant();
        if (op is "add" or "subtract" or "multiply" or "divide" or "round" or
            "sum" or "min" or "max" or "average")
        {
            EnsureArgumentTypes(operation, argumentTypes, "NUMBER");
            return;
        }

        if (op == "datediffdays")
        {
            EnsureArgumentTypes(operation, argumentTypes, "DATE", "FULL_DATE");
            return;
        }

        if (op == "dateadddays")
        {
            EnsureArgumentType(operation, argumentTypes.ElementAtOrDefault(0), "DATE", "FULL_DATE");
            EnsureArgumentType(operation, argumentTypes.ElementAtOrDefault(1), "NUMBER");
            return;
        }

        if (op is "and" or "or" or "not")
        {
            EnsureArgumentTypes(operation, argumentTypes, "BOOLEAN");
            return;
        }

        if (op is "concat" or "textjoin" or "trim" or "upper" or "lower")
        {
            EnsureArgumentTypes(operation, argumentTypes, "TEXT", "SINGLE_SELECT", "MULTI_SELECT");
            return;
        }

        if (op == "if")
        {
            EnsureArgumentType(operation, argumentTypes.ElementAtOrDefault(0), "BOOLEAN");
            EnsureCompatibleResultTypes(operation, argumentTypes.Skip(1));
            return;
        }

        if (op is "firstnonblank" or "coalesce")
        {
            EnsureCompatibleResultTypes(operation, argumentTypes);
            return;
        }

        if (op is "eq" or "gt" or "gte" or "lt" or "lte")
            EnsureCompatibleResultTypes(operation, argumentTypes);
    }

    private static void EnsureArgumentTypes(
        string operation,
        IEnumerable<string?> argumentTypes,
        params string[] allowedTypes)
    {
        foreach (var argumentType in argumentTypes)
            EnsureArgumentType(operation, argumentType, allowedTypes);
    }

    private static void EnsureArgumentType(
        string operation,
        string? argumentType,
        params string[] allowedTypes)
    {
        if (string.IsNullOrWhiteSpace(argumentType) ||
            allowedTypes.Contains(argumentType, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_INPUT_TYPE_MISMATCH",
            $"{operation}:{argumentType}");
    }

    private static string? InferPassthroughType(
        string operation,
        IReadOnlyList<string?> argumentTypes)
    {
        var op = operation.Trim().ToLowerInvariant();
        var candidates = op switch
        {
            "copy" or "firstnonblank" or "coalesce" => argumentTypes,
            "if" => argumentTypes.Skip(1).ToList(),
            _ => Array.Empty<string?>()
        };
        var normalized = candidates
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0)
            return null;
        if (normalized.Count == 1)
            return normalized[0];
        if (normalized.All(type => type is "DATE" or "FULL_DATE"))
            return "DATE";
        return "JSON";
    }

    private static void EnsureCompatibleResultTypes(
        string operation,
        IEnumerable<string?> argumentTypes)
    {
        var normalized = argumentTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => DynamicFlowMappingFieldValueContract.NormalizeDataType(type!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count <= 1 ||
            normalized.All(type => type is "DATE" or "FULL_DATE"))
        {
            return;
        }

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_INPUT_TYPE_MISMATCH",
            $"{operation}:{string.Join(",", normalized)}");
    }

    private static string? InferOperationType(string operation)
        => operation.Trim().ToLowerInvariant() switch
        {
            "add" or "subtract" or "multiply" or "divide" or "round" or "sum" or "count" or
                "min" or "max" or "average" or "distinctcount" or "datediffdays" => "NUMBER",
            "concat" or "textjoin" or "trim" or "upper" or "lower" => "TEXT",
            "dateadddays" => "DATE",
            "and" or "or" or "not" or "eq" or "gt" or "gte" or "lt" or "lte" => "BOOLEAN",
            "tojson" => "JSON",
            _ => null
        };

    private static string? InferLiteralType(JsonNode? value)
    {
        if (value is JsonArray or JsonObject)
            return "JSON";
        if (value is not JsonValue jsonValue)
            return null;
        if (jsonValue.TryGetValue<bool>(out _))
            return "BOOLEAN";
        if (jsonValue.TryGetValue<decimal>(out _) || jsonValue.TryGetValue<double>(out _))
            return "NUMBER";
        return jsonValue.TryGetValue<string>(out _) ? "TEXT" : null;
    }

    private static bool IsBlank(JsonNode? value)
    {
        if (value is null)
            return true;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text);
        if (value is JsonArray array)
            return array.Count == 0;
        return string.Equals(value.ToJsonString(), "null", StringComparison.Ordinal);
    }

    private static string ReadText(JsonNode? value, string operation)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text;

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_TEXT_INVALID",
            operation);
    }

    private static string StableJson(JsonNode? value)
        => value?.ToJsonString() ?? "null";

    private static string? ReadString(JsonObject root, string propertyName)
        => root[propertyName] is JsonValue value &&
           value.TryGetValue<string>(out var text) &&
           !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static string? ReadLiteralString(JsonObject root, string propertyName)
        => root[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
}

internal static class DynamicFlowMappingFieldFailureReasons
{
    public const string DataTypeRequired = "DYNAMIC_FLOW_MAPPING_FIELD_DATA_TYPE_REQUIRED";
    public const string DataTypeUnsupported = "DYNAMIC_FLOW_MAPPING_FIELD_DATA_TYPE_UNSUPPORTED";
    public const string ResultTypeMismatch = "DYNAMIC_FLOW_MAPPING_FIELD_RESULT_TYPE_MISMATCH";
    public const string ValueKindInvalid = "DYNAMIC_FLOW_MAPPING_FIELD_VALUE_KIND_INVALID";
    public const string DateInvalid = "DYNAMIC_FLOW_MAPPING_FIELD_DATE_INVALID";
    public const string ChoiceCodeInvalid = "DYNAMIC_FLOW_MAPPING_FIELD_CHOICE_CODE_INVALID";
    public const string ChoiceItemBlank = "DYNAMIC_FLOW_MAPPING_FIELD_CHOICE_ITEM_BLANK";
    public const string ChoiceItemDuplicate = "DYNAMIC_FLOW_MAPPING_FIELD_CHOICE_ITEM_DUPLICATE";
    public const string NullNotAllowed = "DYNAMIC_FLOW_MAPPING_FIELD_NULL_NOT_ALLOWED";
    public const string NullPolicyInvalid = "DYNAMIC_FLOW_MAPPING_FIELD_NULL_POLICY_INVALID";
    public const string NullPolicyTypeMismatch = "DYNAMIC_FLOW_MAPPING_FIELD_NULL_POLICY_TYPE_MISMATCH";
    public const string ConflictPolicyInvalid = "DYNAMIC_FLOW_MAPPING_FIELD_CONFLICT_POLICY_INVALID";
    public const string Conflict = "DYNAMIC_FLOW_MAPPING_FIELD_CONFLICT";
}

internal sealed record DynamicFlowMappingFieldEvaluationResult(
    JsonNode? ProposedValue,
    bool ShouldWrite,
    string Disposition,
    string EvaluatorVersion,
    string FunctionRegistryVersion,
    string FunctionRegistrySha256);

internal static class DynamicFlowMappingFieldContract
{
    public static DynamicFlowMappingFieldEvaluationResult Evaluate(
        JsonNode? expression,
        IReadOnlyDictionary<string, string> inputDataTypes,
        IReadOnlyDictionary<string, JsonNode?> inputs,
        string targetDataType,
        string nullPolicy = "KEEP_NULL",
        string conflictPolicy = "OVERWRITE",
        JsonNode? currentTargetValue = null,
        IReadOnlyCollection<string>? targetChoiceCodes = null)
    {
        var normalizedTargetType =
            DynamicFlowMappingFieldValueContract.NormalizeDataType(targetDataType);
        var resultDataType =
            DynamicFlowMappingExpressionEvaluator.Validate(expression, inputDataTypes);
        if (!string.IsNullOrWhiteSpace(resultDataType) &&
            !DynamicFlowMappingFieldValueContract.AreCompatible(
                resultDataType,
                normalizedTargetType))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.ResultTypeMismatch,
                $"{resultDataType}:{normalizedTargetType}");
        }

        var proposedValue = DynamicFlowMappingExpressionEvaluator.Evaluate(
            expression,
            inputs,
            inputDataTypes);

        var normalizedNullPolicy = NormalizePolicy(nullPolicy);
        if (proposedValue is null)
        {
            switch (normalizedNullPolicy)
            {
                case "KEEP_NULL":
                    break;
                case "SKIP":
                    return Result(null, shouldWrite: false, "SKIP_NULL");
                case "ERROR":
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingFieldFailureReasons.NullNotAllowed);
                case "ZERO":
                    if (normalizedTargetType != "NUMBER")
                    {
                        throw new DynamicFlowMappingEvaluationException(
                            DynamicFlowMappingFieldFailureReasons.NullPolicyTypeMismatch,
                            $"{normalizedNullPolicy}:{normalizedTargetType}");
                    }

                    proposedValue = JsonValue.Create(0m);
                    break;
                case "EMPTY_TEXT":
                    if (normalizedTargetType != "TEXT")
                    {
                        throw new DynamicFlowMappingEvaluationException(
                            DynamicFlowMappingFieldFailureReasons.NullPolicyTypeMismatch,
                            $"{normalizedNullPolicy}:{normalizedTargetType}");
                    }

                    proposedValue = JsonValue.Create(string.Empty);
                    break;
                default:
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingFieldFailureReasons.NullPolicyInvalid);
            }
        }
        else if (normalizedNullPolicy is not ("KEEP_NULL" or "SKIP" or "ERROR" or "ZERO" or "EMPTY_TEXT"))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.NullPolicyInvalid);
        }

        DynamicFlowMappingFieldValueContract.ValidateJsonNative(
            proposedValue,
            normalizedTargetType,
            targetChoiceCodes,
            allowNull: true);
        DynamicFlowMappingFieldValueContract.ValidateJsonNative(
            currentTargetValue,
            normalizedTargetType,
            targetChoiceCodes,
            allowNull: true);

        if (StableJson(currentTargetValue) == StableJson(proposedValue))
            return Result(proposedValue, shouldWrite: false, "UNCHANGED");

        if (currentTargetValue is not null)
        {
            switch (NormalizePolicy(conflictPolicy))
            {
                case "OVERWRITE":
                    break;
                case "TARGET_WINS":
                    return Result(proposedValue, shouldWrite: false, "TARGET_WINS");
                case "ERROR_ON_CONFLICT":
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingFieldFailureReasons.Conflict);
                default:
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingFieldFailureReasons.ConflictPolicyInvalid);
            }
        }
        else if (NormalizePolicy(conflictPolicy) is not ("OVERWRITE" or "TARGET_WINS" or "ERROR_ON_CONFLICT"))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.ConflictPolicyInvalid);
        }

        return Result(proposedValue, shouldWrite: true, "WRITE");
    }

    private static DynamicFlowMappingFieldEvaluationResult Result(
        JsonNode? value,
        bool shouldWrite,
        string disposition)
        => new(
            value?.DeepClone(),
            shouldWrite,
            disposition,
            DynamicFlowMappingExpressionEvaluator.EvaluatorVersion,
            DynamicFlowRegisteredFunctionRegistry.RegistryVersion,
            DynamicFlowRegisteredFunctionRegistry.RegistrySha256);

    private static string NormalizePolicy(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

    private static string StableJson(JsonNode? value)
        => value?.ToJsonString() ?? "null";
}

internal static class DynamicFlowMappingFieldValueContract
{
    private static readonly string[] SupportedDataTypes =
    {
        "TEXT", "NUMBER", "BOOLEAN", "DATE", "FULL_DATE",
        "SINGLE_SELECT", "MULTI_SELECT", "JSON"
    };

    public static string NormalizeDataType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.DataTypeRequired);
        }

        var normalized = dataType.Trim().ToUpperInvariant();
        if (!SupportedDataTypes.Contains(normalized, StringComparer.Ordinal))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.DataTypeUnsupported,
                normalized);
        }

        return normalized;
    }

    public static bool AreCompatible(string left, string right)
    {
        var normalizedLeft = NormalizeDataType(left);
        var normalizedRight = NormalizeDataType(right);
        return normalizedLeft == normalizedRight ||
               normalizedLeft is ("DATE" or "FULL_DATE") &&
               normalizedRight is ("DATE" or "FULL_DATE");
    }

    public static void ValidateJsonNative(
        JsonNode? value,
        string dataType,
        IReadOnlyCollection<string>? allowedChoiceCodes,
        bool allowNull)
    {
        var normalizedType = NormalizeDataType(dataType);
        if (value is null)
        {
            if (allowNull)
                return;

            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.NullNotAllowed);
        }

        JsonElement element;
        try
        {
            using var document = JsonDocument.Parse(value.ToJsonString());
            element = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.ValueKindInvalid,
                normalizedType);
        }

        switch (normalizedType)
        {
            case "JSON":
                return;
            case "TEXT":
                RequireKind(element, normalizedType, JsonValueKind.String);
                return;
            case "NUMBER":
                RequireKind(element, normalizedType, JsonValueKind.Number);
                if (!element.TryGetDecimal(out _))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingFieldFailureReasons.ValueKindInvalid,
                        normalizedType);
                }

                return;
            case "BOOLEAN":
                RequireKind(element, normalizedType, JsonValueKind.True, JsonValueKind.False);
                return;
            case "DATE":
            case "FULL_DATE":
                RequireKind(element, normalizedType, JsonValueKind.String);
                var date = element.GetString() ?? string.Empty;
                var formats = normalizedType == "FULL_DATE"
                    ? new[] { "dd/MM/yyyy" }
                    : new[] { "yyyy", "MM/yyyy", "dd/MM/yyyy" };
                if (!DateTime.TryParseExact(
                        date,
                        formats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingFieldFailureReasons.DateInvalid,
                        normalizedType);
                }

                return;
            case "SINGLE_SELECT":
                RequireKind(element, normalizedType, JsonValueKind.String);
                ValidateChoiceCode(
                    element.GetString() ?? string.Empty,
                    allowedChoiceCodes,
                    index: null);
                return;
            case "MULTI_SELECT":
                RequireKind(element, normalizedType, JsonValueKind.Array);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new DynamicFlowMappingEvaluationException(
                            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid,
                            $"{normalizedType}:{index}");
                    }

                    var code = item.GetString() ?? string.Empty;
                    ValidateChoiceCode(code, allowedChoiceCodes, index);
                    if (!seen.Add(code))
                    {
                        throw new DynamicFlowMappingEvaluationException(
                            DynamicFlowMappingFieldFailureReasons.ChoiceItemDuplicate,
                            index.ToString(CultureInfo.InvariantCulture));
                    }

                    index++;
                }

                return;
        }
    }

    private static void ValidateChoiceCode(
        string code,
        IReadOnlyCollection<string>? allowedChoiceCodes,
        int? index)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.ChoiceItemBlank,
                index?.ToString(CultureInfo.InvariantCulture));
        }

        if (allowedChoiceCodes is not null &&
            !allowedChoiceCodes.Contains(code, StringComparer.Ordinal))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.ChoiceCodeInvalid,
                index is null
                    ? code
                    : $"{index.Value.ToString(CultureInfo.InvariantCulture)}:{code}");
        }
    }

    private static void RequireKind(
        JsonElement element,
        string dataType,
        params JsonValueKind[] expectedKinds)
    {
        if (expectedKinds.Contains(element.ValueKind))
            return;

        throw new DynamicFlowMappingEvaluationException(
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid,
            $"{dataType}:{element.ValueKind}");
    }
}

internal static class DynamicFlowRegisteredFunctionRegistry
{
    public const string RegistryVersion = "P7-FUNC-1";

    private sealed record FunctionDescriptor(
        string Code,
        int Version,
        string ResultDataType,
        IReadOnlyDictionary<string, string> RequiredArguments,
        Func<IReadOnlyDictionary<string, JsonNode?>, JsonNode?> Execute);

    private static readonly IReadOnlyDictionary<string, FunctionDescriptor> Functions =
        new Dictionary<string, FunctionDescriptor>(StringComparer.Ordinal)
        {
            [BuildKey("CALC_PERCENTAGE", 1)] = new(
                "CALC_PERCENTAGE",
                1,
                "NUMBER",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["numerator"] = "NUMBER",
                    ["denominator"] = "NUMBER"
                },
                CalculatePercentage)
        };

    public static string CanonicalDescriptor { get; } = BuildCanonicalDescriptor();
    public static string RegistrySha256 { get; } = ComputeRegistrySha256();
    public static string RegistryHash => RegistrySha256;

    public static void EnsurePin(string? registryVersion, string? registrySha256)
    {
        if (string.Equals(registryVersion, RegistryVersion, StringComparison.Ordinal) &&
            string.Equals(registrySha256, RegistrySha256, StringComparison.Ordinal))
        {
            return;
        }

        throw new DynamicFlowMappingEvaluationException(
            "DYNAMIC_FLOW_MAPPING_FUNCTION_REGISTRY_PIN_MISMATCH");
    }

    public static string Validate(
        string? functionCode,
        int? functionVersion,
        IReadOnlyDictionary<string, JsonNode?>? arguments,
        IReadOnlyDictionary<string, string> inputDataTypes)
    {
        var descriptor = Resolve(functionCode, functionVersion);
        arguments ??= new Dictionary<string, JsonNode?>();
        foreach (var requiredArgument in descriptor.RequiredArguments)
        {
            if (!arguments.TryGetValue(requiredArgument.Key, out var expression) || expression is null)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_FUNCTION_ARGUMENT_REQUIRED",
                    requiredArgument.Key);
            }

            var actualType = DynamicFlowMappingExpressionEvaluator.Validate(expression, inputDataTypes);
            if (!string.IsNullOrWhiteSpace(actualType) &&
                !string.Equals(actualType, requiredArgument.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_FUNCTION_ARGUMENT_TYPE_MISMATCH",
                    $"{requiredArgument.Key}:{actualType}:{requiredArgument.Value}");
            }
        }

        var unknownArguments = arguments.Keys
            .Except(descriptor.RequiredArguments.Keys, StringComparer.Ordinal)
            .ToList();
        if (unknownArguments.Count > 0)
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_FUNCTION_ARGUMENT_UNKNOWN",
                unknownArguments[0]);
        }

        return descriptor.ResultDataType;
    }

    public static JsonNode? Execute(
        string? functionCode,
        int? functionVersion,
        IReadOnlyDictionary<string, JsonNode?> arguments)
        => Resolve(functionCode, functionVersion).Execute(arguments);

    private static FunctionDescriptor Resolve(string? functionCode, int? functionVersion)
    {
        var code = string.IsNullOrWhiteSpace(functionCode)
            ? null
            : functionCode.Trim().ToUpperInvariant();
        var version = functionVersion.GetValueOrDefault();
        if (code is null || version <= 0 || !Functions.TryGetValue(BuildKey(code, version), out var descriptor))
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_FUNCTION_NOT_REGISTERED",
                $"{code ?? "-"}:{version}");
        }

        return descriptor;
    }

    private static JsonNode? CalculatePercentage(IReadOnlyDictionary<string, JsonNode?> arguments)
    {
        var numerator = ReadDecimal(arguments["numerator"], "numerator");
        var denominator = ReadDecimal(arguments["denominator"], "denominator");
        if (denominator == 0m)
            throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_DIVIDE_BY_ZERO");
        return JsonValue.Create(numerator / denominator * 100m);
    }

    private static decimal ReadDecimal(JsonNode? value, string argumentName)
    {
        if (value is JsonArray array)
        {
            var numbers = array
                .Where(item => item is not null)
                .Select(item => ReadDecimal(item, argumentName))
                .ToList();
            return numbers.Count == 0 ? 0m : numbers.Sum();
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<decimal>(out var decimalValue))
                return decimalValue;
            if (jsonValue.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
                return (decimal)doubleValue;
        }

        throw new DynamicFlowMappingEvaluationException("DYNAMIC_FLOW_MAPPING_NUMBER_INVALID", argumentName);
    }

    private static string BuildCanonicalDescriptor()
        => string.Join(
            "\n",
            new[] { RegistryVersion }.Concat(
                Functions.Values
                    .OrderBy(descriptor => descriptor.Code, StringComparer.Ordinal)
                    .ThenBy(descriptor => descriptor.Version)
                    .Select(descriptor =>
                        string.Join(
                            "|",
                            descriptor.Code,
                            descriptor.Version.ToString(CultureInfo.InvariantCulture),
                            descriptor.ResultDataType,
                            string.Join(
                                ",",
                                descriptor.RequiredArguments
                                    .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                                    .Select(argument => $"{argument.Key}:{argument.Value}"))))));

    private static string ComputeRegistrySha256()
        => Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDescriptor)))
            .ToLowerInvariant();

    private static string BuildKey(string code, int version)
        => $"{code.Trim().ToUpperInvariant()}:{version}";
}
