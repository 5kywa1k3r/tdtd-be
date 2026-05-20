using System.Globalization;
using System.Text.Json;
using tdtd_be.Common.Errors;

namespace tdtd_be.Services.WorkAssignments.Domain;

public static class WorkAssignmentAutoApproveConditionNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string? NormalizeOrNull(string? conditionJson, string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(conditionJson))
            return null;

        try
        {
            using var conditionDocument = JsonDocument.Parse(conditionJson);
            var root = conditionDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidCondition("Điều kiện tự duyệt phải là JSON object.", new { field = "autoApproveConditionJson" });

            if (root.TryGetProperty("enabled", out var enabledElement) &&
                enabledElement.ValueKind is JsonValueKind.False)
            {
                return null;
            }

            var fieldId = ReadString(root, "fieldId");
            var fieldKey = ReadString(root, "fieldKey");
            var field = ResolveField(fieldsJson, fieldId, fieldKey)
                        ?? throw InvalidCondition(
                            "Điều kiện tự duyệt phải chọn field hợp lệ của biểu mẫu.",
                            new { fieldId, fieldKey });

            if (!IsSupportedFieldType(field.Type))
            {
                throw InvalidCondition(
                    "Điều kiện tự duyệt chỉ hỗ trợ field số, chọn một hoặc chọn nhiều.",
                    new { fieldId = field.Id, fieldKey = field.Key, fieldType = field.Type });
            }

            var op = NormalizeOperator(ReadString(root, "operator"));
            EnsureOperatorAllowed(field.Type, op);

            object? normalizedValue = null;
            if (op != "notEmpty")
            {
                if (!TryGetPropertyCaseInsensitive(root, "value", out var valueElement) ||
                    IsBlankValue(valueElement))
                {
                    throw InvalidCondition(
                        "Điều kiện tự duyệt phải có giá trị so sánh.",
                        new { fieldId = field.Id, fieldKey = field.Key, fieldType = field.Type, op });
                }

                normalizedValue = NormalizeConditionValue(field.Type, valueElement);
            }

            return JsonSerializer.Serialize(new
            {
                version = 1,
                enabled = true,
                fieldId = field.Id,
                fieldKey = field.Key,
                fieldType = field.Type,
                @operator = op,
                value = normalizedValue
            }, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw InvalidCondition(
                "Điều kiện tự duyệt không phải JSON hợp lệ.",
                new { field = "autoApproveConditionJson", ex.Message });
        }
    }

    public static bool Matches(string? conditionJson, string? fieldValuesJson)
    {
        if (string.IsNullOrWhiteSpace(conditionJson) || string.IsNullOrWhiteSpace(fieldValuesJson))
            return false;

        try
        {
            using var conditionDocument = JsonDocument.Parse(conditionJson);
            using var valuesDocument = JsonDocument.Parse(fieldValuesJson);
            var condition = conditionDocument.RootElement;
            if (condition.ValueKind != JsonValueKind.Object)
                return false;

            if (condition.TryGetProperty("enabled", out var enabledElement) &&
                enabledElement.ValueKind is JsonValueKind.False)
            {
                return false;
            }

            var fieldId = ReadString(condition, "fieldId");
            var fieldKey = ReadString(condition, "fieldKey");
            var fieldType = NormalizeFieldType(ReadString(condition, "fieldType"));
            var op = NormalizeOperator(ReadString(condition, "operator"));
            if (string.IsNullOrWhiteSpace(fieldId) && string.IsNullOrWhiteSpace(fieldKey))
                return false;

            if (!IsSupportedFieldType(fieldType))
                return false;

            if (!TryGetReportValue(valuesDocument.RootElement, fieldId, fieldKey, out var reportValue))
                return false;

            if (op == "notEmpty")
                return !IsBlankValue(reportValue);

            if (IsBlankValue(reportValue) ||
                !TryGetPropertyCaseInsensitive(condition, "value", out var conditionValue) ||
                IsBlankValue(conditionValue))
            {
                return false;
            }

            return Compare(fieldType, op, reportValue, conditionValue);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object NormalizeConditionValue(string fieldType, JsonElement value)
    {
        fieldType = NormalizeFieldType(fieldType);
        if (!IsSupportedFieldType(fieldType))
        {
            throw InvalidCondition(
                "Điều kiện tự duyệt chỉ hỗ trợ field số, chọn một hoặc chọn nhiều.",
                new { fieldType });
        }

        if (fieldType == "number")
        {
            var number = ToNullableDecimal(value);
            if (!number.HasValue)
                throw InvalidCondition("Giá trị điều kiện tự duyệt phải là số.", new { fieldType });
            return number.Value;
        }

        var text = ToNullableString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw InvalidCondition("Giá trị điều kiện tự duyệt không được trống.", new { fieldType });

        return text;
    }

    private static bool Compare(
        string fieldType,
        string op,
        JsonElement reportValue,
        JsonElement conditionValue)
    {
        fieldType = NormalizeFieldType(fieldType);
        if (!IsSupportedFieldType(fieldType))
            return false;

        if (fieldType == "number")
        {
            var left = ToNullableDecimal(reportValue);
            var right = ToNullableDecimal(conditionValue);
            return left.HasValue && right.HasValue && CompareComparable(left.Value, right.Value, op);
        }

        var expected = ToNullableString(conditionValue)?.Trim();
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        if (fieldType == "multiSelect")
        {
            var values = ToStringArray(reportValue);
            var has = values.Any(value => StringEquals(value, expected));
            return op switch
            {
                "eq" or "contains" => has,
                "neq" => !has,
                _ => false
            };
        }

        var actual = ToNullableString(reportValue)?.Trim();
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        return op switch
        {
            "eq" => StringEquals(actual, expected),
            "neq" => !StringEquals(actual, expected),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool CompareComparable<T>(T left, T right, string op)
        where T : IComparable<T>
        => op switch
        {
            "eq" => left.CompareTo(right) == 0,
            "neq" => left.CompareTo(right) != 0,
            "gt" => left.CompareTo(right) > 0,
            "gte" => left.CompareTo(right) >= 0,
            "lt" => left.CompareTo(right) < 0,
            "lte" => left.CompareTo(right) <= 0,
            _ => false
        };

    private static void EnsureOperatorAllowed(string fieldType, string op)
    {
        fieldType = NormalizeFieldType(fieldType);
        var allowed = fieldType switch
        {
            "number" => new[] { "eq", "neq", "gt", "gte", "lt", "lte", "notEmpty" },
            "singleSelect" => new[] { "eq", "neq", "notEmpty" },
            "multiSelect" => new[] { "contains", "eq", "neq", "notEmpty" },
            _ => Array.Empty<string>()
        };

        if (!allowed.Contains(op, StringComparer.Ordinal))
            throw InvalidCondition(
                "Toán tử điều kiện tự duyệt không phù hợp với kiểu field.",
                new { fieldType, op, allowed });
    }

    private static string NormalizeOperator(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "eq";

        var lower = normalized.ToLowerInvariant();
        var canonical = lower switch
        {
            "==" or "=" or "equals" => "eq",
            "!=" or "<>" or "notequals" or "not_equals" => "neq",
            ">" or "greaterthan" or "greater_than" => "gt",
            ">=" or "greaterorequal" or "greater_or_equal" or "greaterthanorequal" => "gte",
            "<" or "lessthan" or "less_than" => "lt",
            "<=" or "lessorequal" or "less_or_equal" or "lessthanorequal" => "lte",
            "contains" => "contains",
            "notempty" or "not_empty" or "isnotempty" or "is_not_empty" => "notEmpty",
            "eq" or "neq" or "gt" or "gte" or "lt" or "lte" => lower,
            _ => throw InvalidCondition("Toán tử điều kiện tự duyệt không hợp lệ.", new { op = value })
        };

        return canonical;
    }

    private static AutoApproveField? ResolveField(string? fieldsJson, string? fieldId, string? fieldKey)
    {
        foreach (var field in ReadFields(fieldsJson))
        {
            if (!string.IsNullOrWhiteSpace(fieldId) &&
                string.Equals(field.Id, fieldId.Trim(), StringComparison.Ordinal))
            {
                return field;
            }

            if (!string.IsNullOrWhiteSpace(fieldKey) &&
                string.Equals(field.Key, fieldKey.Trim(), StringComparison.Ordinal))
            {
                return field;
            }
        }

        return null;
    }

    private static List<AutoApproveField> ReadFields(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new List<AutoApproveField>();

        try
        {
            using var document = JsonDocument.Parse(fieldsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new List<AutoApproveField>();

            var fields = new List<AutoApproveField>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var key = ReadString(item, "key") ?? id;
                var type = NormalizeFieldType(ReadString(item, "type"));
                fields.Add(new AutoApproveField(id, key, type));
            }

            return fields;
        }
        catch (JsonException)
        {
            return new List<AutoApproveField>();
        }
    }

    private static bool TryGetReportValue(
        JsonElement root,
        string? fieldId,
        string? fieldKey,
        out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        var valuesRoot = root;
        if (TryGetPropertyCaseInsensitive(root, "values", out var nestedValues) &&
            nestedValues.ValueKind == JsonValueKind.Object)
        {
            valuesRoot = nestedValues;
        }

        if (!string.IsNullOrWhiteSpace(fieldId) &&
            TryGetPropertyCaseInsensitive(valuesRoot, fieldId.Trim(), out value))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fieldKey) &&
            TryGetPropertyCaseInsensitive(valuesRoot, fieldKey.Trim(), out value))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeFieldType(string? value)
    {
        var normalized = value?.Trim();
        return normalized switch
        {
            "number" => "number",
            "date" => "date",
            "fullDate" => "fullDate",
            "boolean" => "boolean",
            "shortText" => "shortText",
            "longText" => "longText",
            "stringList" => "stringList",
            "singleSelect" => "singleSelect",
            "multiSelect" => "multiSelect",
            _ => "shortText"
        };
    }

    private static bool IsSupportedFieldType(string fieldType)
        => NormalizeFieldType(fieldType) is "number" or "singleSelect" or "multiSelect";

    private static bool TryGetPropertyCaseInsensitive(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetPropertyCaseInsensitive(element, name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static bool IsBlankValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => !value.EnumerateArray().Any(),
            _ => false
        };

    private static decimal? ToNullableDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? ToNullableBoolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();

        if (value.ValueKind == JsonValueKind.String)
        {
            var normalized = value.GetString()?.Trim().ToLowerInvariant();
            return normalized switch
            {
                "true" or "1" or "yes" or "y" or "co" or "có" => true,
                "false" or "0" or "no" or "n" or "khong" or "không" => false,
                _ => null
            };
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number switch
            {
                1 => true,
                0 => false,
                _ => null
            };
        }

        return null;
    }

    private static DateTime? ToNullableDate(JsonElement value)
    {
        var text = ToNullableString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var formats = new[] { "dd/MM/yyyy", "MM/yyyy", "yyyy", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss.FFFK" };
        if (DateTime.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            return exact.Date;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : null;
    }

    private static List<string> ToStringArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        }

        if (value.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return value
            .EnumerateArray()
            .Select(ToNullableString)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToList();
    }

    private static string? ToNullableString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static bool StringEquals(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static AppException InvalidCondition(string message, object? details = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                message,
                details
            });

    private sealed record AutoApproveField(string Id, string Key, string Type);
}
