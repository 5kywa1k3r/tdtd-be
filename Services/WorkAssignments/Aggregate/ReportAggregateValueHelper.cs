using System.Text.Json;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

internal static class ReportAggregateValueHelper
{
    public static bool TryToDecimal(object? value, out decimal result)
    {
        result = 0m;
        if (value == null) return false;

        switch (value)
        {
            case decimal d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case double db:
                result = (decimal)db;
                return true;
            case float f:
                result = (decimal)f;
                return true;
            case string s when decimal.TryParse(s, out var parsed):
                result = parsed;
                return true;
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Number && je.TryGetDecimal(out var d2))
                {
                    result = d2;
                    return true;
                }
                if (je.ValueKind == JsonValueKind.String && decimal.TryParse(je.GetString(), out var d3))
                {
                    result = d3;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    public static object NormalizeNumber(decimal value)
    {
        if (decimal.Truncate(value) == value)
            return (long)value;

        return value;
    }

    public static Dictionary<string, object?> ToDictionary(object? value)
    {
        if (value == null)
            return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in je.EnumerateObject())
            {
                result[prop.Name] = ConvertJsonElement(prop.Value);
            }
            return result;
        }

        return new Dictionary<string, object?>();
    }

    public static List<object?> ToList(object? value)
    {
        if (value == null)
            return new List<object?>();

        if (value is List<object?> list)
            return list;

        if (value is object[] arr)
            return arr.Cast<object?>().ToList();

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return je.EnumerateArray().Select(ConvertJsonElement).ToList();
        }

        return new List<object?>();
    }

    public static object? ConvertJsonElement(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetDecimal(out var d) ? d : e.GetRawText(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Array => e.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(x => x.Name, x => ConvertJsonElement(x.Value)),
            _ => e.GetRawText()
        };
    }
}