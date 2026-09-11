using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Common.Errors;

namespace tdtd_be.Services.StatisticsReconciliation;

public static class StatisticReconciliationCanonicalJson
{
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static T DeserializeStrict<T>(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw InvalidSchema("$", "OBJECT_REQUIRED");
        EnsureNoDuplicateProperties(body, "$", 0);
        try
        {
            return JsonSerializer.Deserialize<T>(body.GetRawText(), StrictOptions)
                   ?? throw InvalidSchema("$", "BODY_REQUIRED");
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidSchema(exception.Path ?? "$", "STRICT_SCHEMA_INVALID");
        }
    }

    public static string HashObject<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, StrictOptions);
        return HashText(Canonicalize(element));
    }

    public static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string HashElement(JsonElement? value)
        => value.HasValue
            ? HashObject(value.Value)
            : HashText("P10_RECONCILIATION_EMPTY_FILTER_V1");

    public static string HashText(string value)
        => HashBytes(Encoding.UTF8.GetBytes(value));

    public static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void EnsureNoDuplicateProperties(JsonElement value, string path, int depth)
    {
        if (depth > 64)
            throw InvalidSchema(path, "MAX_DEPTH_EXCEEDED");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw InvalidSchema($"{path}.{property.Name}", "DUPLICATE_PROPERTY");
                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                EnsureNoDuplicateProperties(item, $"{path}[{index++}]", depth + 1);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw InvalidSchema("$", "JSON_TOKEN_INVALID");
        }
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static AppException InvalidSchema(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_RECONCILIATION_SCHEMA_INVALID,
            new { path, reason, writes = 0 });
}
