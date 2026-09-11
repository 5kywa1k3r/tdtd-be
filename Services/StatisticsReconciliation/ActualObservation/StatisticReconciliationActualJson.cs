using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualJson
{
    internal const int MaxJsonUtf8Bytes = 16 * 1024 * 1024;

    internal static JsonDocument ParseStrict(string? json, string name)
    {
        if (json is null)
            throw Fail($"{name}_REQUIRED");
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonUtf8Bytes)
            throw Fail($"{name}_TOO_LARGE");
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            ValidateNoDuplicateProperties(document.RootElement, "$", name);
            return document;
        }
        catch (JsonException)
        {
            throw Fail($"{name}_INVALID");
        }
    }

    internal static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
        {
            WriteCanonical(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string RawSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    internal static string CanonicalSha256(JsonElement value)
        => RawSha256(Canonicalize(value));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (value.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(value.GetDouble());
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
                throw Fail("JSON_TOKEN_UNSUPPORTED");
        }
    }

    private static void ValidateNoDuplicateProperties(
        JsonElement value,
        string path,
        string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Fail($"{name}_DUPLICATE_PROPERTY:{path}.{property.Name}");
                ValidateNoDuplicateProperties(property.Value, $"{path}.{property.Name}", name);
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.Array)
            return;
        var index = 0;
        foreach (var item in value.EnumerateArray())
            ValidateNoDuplicateProperties(item, $"{path}[{index++}]", name);
    }

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);
}
